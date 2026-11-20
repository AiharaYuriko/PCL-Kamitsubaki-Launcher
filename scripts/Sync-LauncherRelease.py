import hashlib
import json
import os
from pathlib import Path
import re
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError


EXE_NAME = "PCL-Kamitsubaki.exe"
MANIFEST_NAME = "update.json"
LAUNCHER_PATH = "pcl/launcher"
WORKER_URL = "https://s3-sign.kaf.mom"


def required_env(name):
    value = os.environ.get(name, "").strip()
    if not value:
        raise ValueError(f"Missing configuration: {name}.")
    return value


def read_release(directory, tag, repo):
    manifest = json.loads((directory / MANIFEST_NAME).read_text(encoding="utf-8-sig"))
    expected_url = f"https://github.com/{repo}/releases/download/{tag}/{EXE_NAME}"
    if (manifest.get("productId") != "kamitsubaki-pcl"
            or manifest.get("versionName") != tag[1:]
            or type(manifest.get("versionCode")) is not int
            or manifest["versionCode"] < 1
            or manifest.get("downloadUrl") != expected_url):
        raise ValueError("The update manifest does not match the published release.")
    with (directory / EXE_NAME).open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != str(manifest.get("sha256", "")).lower():
        raise ValueError("The released EXE does not match the manifest SHA-256.")
    return manifest


def publish_manifest(s3, bucket, key, manifest):
    for _ in range(5):
        try:
            current = s3.get_object(Bucket=bucket, Key=key)
        except ClientError as error:
            if error.response["Error"]["Code"] not in {"NoSuchKey", "NotFound", "404"}:
                raise
            condition = {"IfNoneMatch": "*"}
        else:
            with current["Body"] as stream:
                published = json.load(stream)
            if published["versionCode"] > manifest["versionCode"]:
                print("A newer version is already advertised on S3.")
                return
            if published["versionCode"] == manifest["versionCode"]:
                if published == manifest:
                    print("The release is already advertised on S3.")
                    return
                if (published["versionName"] != manifest["versionName"]
                        or published["sha256"].lower() != manifest["sha256"].lower()):
                    raise ValueError("This version code already refers to a different S3 release.")
            condition = {"IfMatch": current["ETag"]}
        try:
            s3.put_object(
                Bucket=bucket, Key=key, Body=json.dumps(manifest, ensure_ascii=False).encode("utf-8"),
                ContentType="application/json", CacheControl="no-store", **condition,
            )
            print(f"Advertised {manifest['versionName']} on S3.")
            return
        except ClientError as error:
            if error.response["ResponseMetadata"]["HTTPStatusCode"] not in {409, 412}:
                raise
    raise RuntimeError("The S3 manifest changed repeatedly during publication.")


def main():
    tag = required_env("RELEASE_TAG")
    if not re.fullmatch(r"v[0-9]+(?:\.[0-9]+){3}", tag):
        raise ValueError("Expected a launcher release tag in vX.Y.Z.W format.")
    directory = Path(required_env("RELEASE_DIRECTORY"))
    manifest = read_release(directory, tag, required_env("GITHUB_REPOSITORY"))
    required_env("AWS_ACCESS_KEY_ID")
    required_env("AWS_SECRET_ACCESS_KEY")
    s3 = boto3.client(
        "s3", endpoint_url=required_env("S3_ENDPOINT"), region_name=required_env("S3_REGION"),
        config=Config(signature_version="s3v4", s3={"addressing_style": "path"},
                      request_checksum_calculation="when_required",
                      response_checksum_validation="when_required"),
    )
    bucket = required_env("S3_BUCKET")
    prefix = required_env("S3_PREFIX").strip("/")
    exe_path = f"{LAUNCHER_PATH}/releases/{tag}/{EXE_NAME}"
    try:
        with (directory / EXE_NAME).open("rb") as stream:
            s3.put_object(
                Bucket=bucket, Key=f"{prefix}/{exe_path}", Body=stream,
                ContentType="application/octet-stream", IfNoneMatch="*",
            )
    except ClientError as error:
        if error.response["ResponseMetadata"]["HTTPStatusCode"] != 412:
            raise

    manifest["mirrorDownloadUrl"] = f"{WORKER_URL}/{exe_path}"
    request = Request(manifest["mirrorDownloadUrl"], headers={"User-Agent": "PCL GitHub release sync"})
    try:
        with urlopen(request, timeout=30) as response:
            digest = hashlib.file_digest(response, "sha256").hexdigest()
    except HTTPError as error:
        raise RuntimeError(f"The launcher EXE download through the signing Worker returned HTTP {error.code}.") from None
    except (URLError, OSError):
        raise RuntimeError("The launcher EXE could not be read through the signing Worker.") from None
    if digest != manifest["sha256"].lower():
        raise ValueError("The launcher EXE read through the Worker failed SHA-256 verification.")
    publish_manifest(s3, bucket, f"{prefix}/{LAUNCHER_PATH}/{MANIFEST_NAME}", manifest)


if __name__ == "__main__":
    main()
