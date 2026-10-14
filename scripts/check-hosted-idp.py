#!/usr/bin/env python3
"""Checks Shop.Mcp bearer authorization against tokens from a real identity provider.

Hosted mode (Entra ID, Auth0 or any OpenID Connect provider) reads its settings from the environment; nothing is created in the
provider. Register an API whose audience is WT_MCP_RESOURCE and two confidential clients that may request tokens for it: one granted
WT_IDP_ROLE (an Entra app role, or an Auth0 permission), one granted nothing.

    WT_IDP_AUTHORITY        issuer, e.g. https://login.microsoftonline.com/<tenant>/v2.0 or https://<domain>.auth0.com/
    WT_MCP_RESOURCE         the API audience, an https URI, e.g. https://workthrough.example/operations/mcp
    WT_IDP_CLIENT_ID / WT_IDP_CLIENT_SECRET                  the client holding WT_IDP_ROLE
    WT_IDP_OUTSIDER_CLIENT_ID / WT_IDP_OUTSIDER_CLIENT_SECRET  a client holding no role
    WT_IDP_ROLE             role or permission value mapped to executions.read (default workthrough-viewer)
    WT_IDP_ROLE_CLAIM       claim carrying it: roles (Entra, default) or permissions (Auth0)
    WT_IDP_SCOPE            token scope; Entra needs <app ID URI>/.default
    WT_IDP_AUDIENCE_PARAM   set to 1 for providers that take the audience as a token request parameter (Auth0)

--local-keycloak runs the same checks against Keycloak in Docker over HTTPS with tests/Oidc/workthrough-realm.json, using the
ASP.NET Core development certificate; it validates this script, not a hosted provider. Requires docker and dotnet.
"""
import base64
import json
import os
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MCP_URL = "https://localhost:5298"
MCP_PATH = "/operations/mcp"
KEYCLOAK = "https://localhost:18443"
CONTAINER = "workthrough-idp-check"
PROTOCOL_VERSION = "2025-11-25"
# Replaced by a context trusting the development certificate in --local-keycloak mode.
TLS = {"context": ssl.create_default_context()}
EXPECTED_TOOL = "workthrough_search_executions"


def fail(message):
    raise SystemExit(f"FAIL: {message}")


def post(url, data, headers=None):
    request = urllib.request.Request(url, data=data, headers=headers or {}, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=30, context=TLS["context"]) as response:
            return response.status, response.read().decode(), response.headers
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(), error.headers


def wait_for(url, seconds=180):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            urllib.request.urlopen(url, timeout=5, context=TLS["context"])
            return
        except urllib.error.HTTPError:
            return
        except Exception:
            time.sleep(2)
    fail(f"{url} did not answer within {seconds} s")


def token(settings, client_id, secret):
    with urllib.request.urlopen(settings["authority"].rstrip("/") + "/.well-known/openid-configuration", timeout=30,
                                context=TLS["context"]) as response:
        endpoint = json.load(response)["token_endpoint"]
    form = {"grant_type": "client_credentials", "client_id": client_id, "client_secret": secret}
    if settings["scope"]:
        form["scope"] = settings["scope"]
    if settings["audience_param"]:
        form["audience"] = settings["resource"]
    status, body, _ = post(endpoint, urllib.parse.urlencode(form).encode(), {"Content-Type": "application/x-www-form-urlencoded"})
    if status != 200:
        fail(f"the provider refused a token for {client_id} (HTTP {status}): {body[:300]}")
    return json.loads(body)["access_token"]


def claims(access_token):
    payload = access_token.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))


def call(access_token, method, params=None):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}).encode()
    status, text, headers = post(MCP_URL + MCP_PATH, body, {
        "Authorization": f"Bearer {access_token}", "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream", "MCP-Protocol-Version": PROTOCOL_VERSION})
    if status == 200 and "text/event-stream" in (headers.get("Content-Type") or ""):
        text = next(line[6:] for line in text.splitlines() if line.startswith("data: "))
    return status, (json.loads(text) if status == 200 else text), headers


def tools(access_token):
    status, reply, _ = call(access_token, "tools/list")
    if status != 200:
        fail(f"tools/list returned HTTP {status}")
    return {tool["name"] for tool in reply["result"]["tools"]}


def check(settings):
    viewer = token(settings, settings["client_id"], settings["client_secret"])
    granted = claims(viewer).get(settings["role_claim"], [])
    if settings["role"] not in (granted if isinstance(granted, list) else [granted]):
        fail(f"the viewer token has no {settings['role']!r} in its {settings['role_claim']!r} claim; check the provider's role assignment")

    names = tools(viewer)
    if EXPECTED_TOOL not in names:
        fail(f"the viewer should see the read tools, saw {sorted(names)}")
    status, reply, _ = call(viewer, "tools/call", {"name": EXPECTED_TOOL, "arguments": {"query": {"limit": 5}}})
    if status != 200 or reply.get("result", {}).get("isError"):
        fail(f"the viewer could not search executions (HTTP {status}): {str(reply)[:300]}")
    print(f"PASS: a {settings['role_claim']} token from {settings['authority']} validates; the mapped role grants {len(names)} read tools.")

    header, payload, signature = viewer.split(".")
    forged = ".".join([header, payload, signature[:-4] + ("AAAA" if not signature.endswith("AAAA") else "BBBB")])
    status, _, headers = call(forged, "tools/list")
    if status != 401 or "resource_metadata" not in (headers.get("WWW-Authenticate") or ""):
        fail(f"a token with a broken signature must be refused with 401 and resource metadata, got HTTP {status}")
    print("PASS: a token with a broken signature is refused with 401 and the resource metadata challenge.")

    outsider = token(settings, settings["outsider_id"], settings["outsider_secret"])
    names = tools(outsider)
    if names:
        fail(f"a token without the role must see no tools, saw {sorted(names)}")
    print("PASS: a valid token without a mapped role authenticates but sees no tools.")


def run_mcp(settings, environment):
    configuration = {
        "Mcp__Authority": settings["authority"], "Mcp__Resource": settings["resource"], "Mcp__Environment": "idp-check",
        "Mcp__EndpointPath": MCP_PATH, "Mcp__RoleClaim": settings["role_claim"],
        f"Mcp__RolePermissions__{settings['role']}__0": "executions.read"}
    return subprocess.Popen(["dotnet", "run", "--project", os.path.join(REPO, "samples", "Shop.Mcp"), "--urls", MCP_URL],
                            env={**environment, **configuration}, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def hosted_settings():
    required = ["WT_IDP_AUTHORITY", "WT_MCP_RESOURCE", "WT_IDP_CLIENT_ID", "WT_IDP_CLIENT_SECRET",
                "WT_IDP_OUTSIDER_CLIENT_ID", "WT_IDP_OUTSIDER_CLIENT_SECRET"]
    missing = [name for name in required if not os.environ.get(name)]
    if missing:
        fail("set " + ", ".join(missing) + " (see this script's header), or pass --local-keycloak")
    return {
        "authority": os.environ["WT_IDP_AUTHORITY"], "resource": os.environ["WT_MCP_RESOURCE"],
        "client_id": os.environ["WT_IDP_CLIENT_ID"], "client_secret": os.environ["WT_IDP_CLIENT_SECRET"],
        "outsider_id": os.environ["WT_IDP_OUTSIDER_CLIENT_ID"], "outsider_secret": os.environ["WT_IDP_OUTSIDER_CLIENT_SECRET"],
        "role": os.environ.get("WT_IDP_ROLE", "workthrough-viewer"), "role_claim": os.environ.get("WT_IDP_ROLE_CLAIM", "roles"),
        "scope": os.environ.get("WT_IDP_SCOPE", ""), "audience_param": os.environ.get("WT_IDP_AUDIENCE_PARAM") == "1"}


def start_keycloak(work):
    certificate, key = os.path.join(work, "localhost.pem"), os.path.join(work, "localhost.key")
    subprocess.run(["dotnet", "dev-certs", "https", "--export-path", certificate, "--format", "Pem", "--no-password"],
                   check=True, capture_output=True)
    os.chmod(key, 0o644)
    TLS["context"] = ssl.create_default_context(cafile=certificate)
    subprocess.run(["docker", "rm", "-f", CONTAINER], capture_output=True)
    subprocess.run(["docker", "run", "-d", "--name", CONTAINER, "-p", "18443:8443",
                    "-v", os.path.join(REPO, "tests", "Oidc") + ":/opt/keycloak/data/import:ro", "-v", work + ":/certs:ro",
                    "quay.io/keycloak/keycloak:26.4", "start-dev", "--import-realm", "--hostname", KEYCLOAK,
                    "--https-certificate-file=/certs/localhost.pem", "--https-certificate-key-file=/certs/localhost.key"],
                   check=True, capture_output=True)
    authority = KEYCLOAK + "/realms/workthrough"
    wait_for(authority + "/.well-known/openid-configuration")
    return {
        "authority": authority, "resource": MCP_URL + MCP_PATH,
        "client_id": "workthrough-mcp-viewer", "client_secret": "mcp-viewer-test-secret",
        "outsider_id": "workthrough-mcp-outsider", "outsider_secret": "mcp-outsider-test-secret",
        "role": "workthrough-viewer", "role_claim": "roles", "scope": "", "audience_param": False}


def main():
    local = "--local-keycloak" in sys.argv[1:]
    work = tempfile.mkdtemp(prefix="workthrough-idp-")
    environment = {**os.environ, "Shop__Sqlite": f"Data Source={os.path.join(work, 'shop.db')}"}
    mcp = None
    try:
        settings = start_keycloak(work) if local else hosted_settings()
        subprocess.run(["dotnet", "run", "--project", os.path.join(REPO, "samples", "Shop.Api"), "--", "--smoke"],
                       env=environment, check=True, capture_output=True)
        mcp = run_mcp(settings, environment)
        wait_for(MCP_URL + MCP_PATH)
        check(settings)
    finally:
        if mcp:
            mcp.terminate()
        if local:
            subprocess.run(["docker", "rm", "-f", CONTAINER], capture_output=True)


if __name__ == "__main__":
    sys.exit(main())
