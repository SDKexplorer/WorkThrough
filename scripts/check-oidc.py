#!/usr/bin/env python3
"""Signs in to the Shop.Dashboard console through a real Keycloak and checks role permissions and tenant scoping.

Starts Keycloak in Docker with tests/Oidc/workthrough-realm.json, fills a fresh schema of DURABLEJOBS_POSTGRES with the Shop smoke orders (tenant-a and
tenant-b), runs Shop.Dashboard over HTTPS with the ASP.NET Core development certificate, and walks the authorization-code flow
as a browser would: the login form, the form_post callback, and the session cookie. Requires docker and dotnet.
"""
import html
import http.cookiejar
import json
import os
import re
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def shop_store():
    """A fresh schema of the disposable test database; the shop's initialization creates it."""
    database = os.environ.get("DURABLEJOBS_POSTGRES") or sys.exit("FAIL: set DURABLEJOBS_POSTGRES to a disposable PostgreSQL database")
    return f"{database};Search Path=wt_oidc_{int(time.time())}_{os.getpid()}"
KEYCLOAK = "http://127.0.0.1:18080"
DASHBOARD = "https://127.0.0.1:5297"
CONTAINER = "workthrough-oidc-check"
INSECURE = ssl._create_unverified_context()


def wait_for(url, seconds=180):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            urllib.request.urlopen(url, context=INSECURE, timeout=5)
            return
        except urllib.error.HTTPError:
            return
        except Exception:
            time.sleep(2)
    raise SystemExit(f"FAIL: {url} did not answer within {seconds} s")


class LoopbackPolicy(http.cookiejar.DefaultCookiePolicy):
    """Sends Secure cookies to loopback HTTP, as browsers treat 127.0.0.1 as a secure context."""

    def return_ok_secure(self, cookie, request):
        return urllib.parse.urlparse(request.get_full_url()).hostname in ("127.0.0.1", "localhost") or super().return_ok_secure(cookie, request)


class Browser:
    """Cookies across both origins and manual redirects, so the form_post callback can be replayed."""

    def __init__(self):
        self.cookies = http.cookiejar.CookieJar(LoopbackPolicy())
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies),
                                                  urllib.request.HTTPSHandler(context=INSECURE), NoRedirect())

    def request(self, url, data=None):
        for _ in range(10):
            body = urllib.parse.urlencode(data).encode() if data is not None else None
            try:
                response = self.opener.open(urllib.request.Request(url, data=body), timeout=30)
                return response.status, response.read().decode(), url
            except urllib.error.HTTPError as error:
                if error.code in (301, 302, 303, 307, 308):
                    url, data = urllib.parse.urljoin(url, error.headers["Location"]), None
                    continue
                return error.code, error.read().decode(), url
        raise SystemExit("FAIL: too many redirects")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def text(page):
    """The readable start of a page, for failure messages."""
    alert = re.search(r'(?s)<(?:span|div)[^>]*(?:kc-feedback-text|input-error|alert-error)[^>]*>(.*?)</', page)
    body = alert.group(1) if alert else re.sub(r"(?s)<(script|style)[^>]*>.*?</\1>", " ", page)
    return re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", html.unescape(body))).strip()[:300]


def form(page):
    action = html.unescape(re.search(r'(?i)<form[^>]*action="([^"]+)"', page).group(1))
    fields = {name: html.unescape(value) for name, value in re.findall(r'(?i)<input[^>]*name="([^"]+)"[^>]*value="([^"]*)"', page)}
    return action, fields


def sign_in(user, password):
    browser = Browser()
    status, page, url = browser.request(DASHBOARD + "/operations/")
    if "kc-form-login" not in page:
        raise SystemExit(f"FAIL: the console did not redirect to the Keycloak login form (HTTP {status} at {url})")
    action, fields = form(page)
    status, page, url = browser.request(urllib.parse.urljoin(url, action), {**fields, "username": user, "password": password})
    if "/signin-oidc" not in page:
        raise SystemExit(f"FAIL: Keycloak did not return the form_post callback for {user} (HTTP {status}): {text(page)}")
    action, fields = form(page)
    status, _, url = browser.request(action, fields)
    if status != 200 or not url.startswith(DASHBOARD):
        raise SystemExit(f"FAIL: the callback did not establish a console session for {user} (HTTP {status} at {url})")
    return browser


def api(browser, path):
    status, body, _ = browser.request(DASHBOARD + "/operations/api" + path)
    return status, (json.loads(body) if status == 200 and body else None)


def main():
    work = tempfile.mkdtemp(prefix="workthrough-oidc-")
    environment = {**os.environ, "Shop__PostgreSql": shop_store()}
    subprocess.run(["docker", "rm", "-f", CONTAINER], capture_output=True)
    subprocess.run(["docker", "run", "-d", "--name", CONTAINER, "-p", "18080:8080",
                    "-e", "KC_BOOTSTRAP_ADMIN_USERNAME=admin", "-e", "KC_BOOTSTRAP_ADMIN_PASSWORD=admin",
                    "-v", os.path.join(REPO, "tests", "Oidc") + ":/opt/keycloak/data/import:ro",
                    "quay.io/keycloak/keycloak:26.4", "start-dev", "--import-realm"], check=True, capture_output=True)
    dashboard = None
    try:
        subprocess.run(["dotnet", "run", "--project", os.path.join(REPO, "samples", "Shop.Api"), "--", "--smoke"],
                       env=environment, check=True, capture_output=True)
        wait_for(KEYCLOAK + "/realms/workthrough/.well-known/openid-configuration")
        dashboard = subprocess.Popen(
            ["dotnet", "run", "--project", os.path.join(REPO, "samples", "Shop.Dashboard"), "--urls", DASHBOARD],
            env={**environment, "ASPNETCORE_ENVIRONMENT": "Development",
                 "Identity__Authority": KEYCLOAK + "/realms/workthrough",
                 "Identity__ClientId": "workthrough-dashboard", "Identity__ClientSecret": "dashboard-test-secret"},
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        wait_for(DASHBOARD + "/operations/")

        operator = sign_in("ada", "ada-test-password")
        status, jobs = api(operator, "/jobs")
        tenants = {job["tenantId"] for job in jobs}
        if status != 200 or not {"tenant-a", "tenant-b"} <= tenants:
            raise SystemExit(f"FAIL: the operator should see both tenants, saw {sorted(t for t in tenants if t)} (HTTP {status})")
        if api(operator, "/audit")[0] != 403:
            raise SystemExit("FAIL: the operator role must not grant audit.read")
        print(f"PASS: operator signed in through Keycloak; roles map to permissions and all {len(jobs)} jobs are visible.")

        viewer = sign_in("grace", "grace-test-password")
        status, jobs = api(viewer, "/jobs")
        if status != 200 or not jobs or {job["tenantId"] for job in jobs} != {"tenant-a"}:
            raise SystemExit(f"FAIL: the tenant-a viewer must see only tenant-a jobs (HTTP {status})")
        print(f"PASS: tenant viewer signed in through Keycloak and sees only its {len(jobs)} tenant-a jobs.")
    finally:
        if dashboard:
            dashboard.terminate()
        subprocess.run(["docker", "rm", "-f", CONTAINER], capture_output=True)


if __name__ == "__main__":
    sys.exit(main())
