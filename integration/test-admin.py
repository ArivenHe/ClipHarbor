"""Real HTTP checks for the management console; uses only the isolated test database."""
import http.cookiejar
import json
import os
import urllib.error
import urllib.request
import uuid

BASE = os.environ.get('CLIPHARBOR_TEST_URL', 'http://localhost:28080').rstrip('/')
PASSWORD = os.environ.get('CLIPHARBOR_TEST_PASSWORD', 'integration-test-only')
checks = 0


def check(value, message):
    global checks
    assert value, message
    checks += 1


class Client:
    def __init__(self):
        self.jar = http.cookiejar.CookieJar()
        self.http = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.csrf = None

    def request(self, path, method='GET', body=None, status=200, headers=None, csrf=True):
        request_headers = dict(headers or {})
        if body is not None:
            request_headers['Content-Type'] = 'application/json'
        if self.csrf and csrf and method != 'GET':
            request_headers['X-CSRF-Token'] = self.csrf
        request = urllib.request.Request(BASE + path, data=None if body is None else json.dumps(body).encode(), method=method, headers=request_headers)
        try:
            response = self.http.open(request, timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        data = response.read()
        check(response.status == status, f'{method} {path}: expected {status}, got {response.status}: {data[:300]}')
        return json.loads(data) if data and 'application/json' in response.headers.get('Content-Type', '') else data

    def login(self, username, password=PASSWORD, status=200):
        result = self.request('/api/v1/admin/login', 'POST', {'username': username, 'password': password}, status=status)
        if status == 200:
            self.csrf = result['csrfToken']
        return result


anonymous, admin, ordinary = Client(), Client(), Client()
anonymous.request('/api/v1/admin/users', status=401)
anonymous.request('/api/v1/admin/users', 'POST', {'username': 'unauthorized', 'password': PASSWORD}, status=401)
ordinary.login('alice', status=401)
admin.login('consoleadmin')
cookie = next(iter(admin.jar))
attributes = {key.lower(): value for key, value in cookie._rest.items()}
check(cookie.path.endswith('/api/v1/admin') and 'httponly' in attributes and attributes.get('samesite', '').lower() == 'strict', 'admin cookie scope and flags')
admin.request('/api/v1/admin/me')
admin.request('/api/v1/admin/users', 'POST', {'username': 'no-csrf', 'password': PASSWORD}, status=403, csrf=False)
admin.request('/api/v1/admin/users', 'POST', {'username': 'bad-origin', 'password': PASSWORD}, status=403, headers={'Origin': 'https://evil.example'})
admin.request('/api/v1/admin/users', 'POST', {'username': 'bad-fetch-site', 'password': PASSWORD}, status=403, headers={'Sec-Fetch-Site': 'cross-site'})
anonymous.request('/api/v1/admin/login', 'POST', {'username': 'consoleadmin', 'password': PASSWORD}, status=403, headers={'Origin': 'https://evil.example'})

name = 'managed-' + uuid.uuid4().hex[:8]
user = admin.request('/api/v1/admin/users', 'POST', {'username': name, 'password': PASSWORD}, status=201)
user_id = user['id']
check(not user['isAdmin'] and user['enabled'] and user['recordCount'] == 0 and user['usedBytes'] == 0, 'new user has independent empty space')
admin.request('/api/v1/admin/users', 'POST', {'username': name.upper(), 'password': PASSWORD}, status=409)
admin.request('/api/v1/admin/users', 'POST', {'username': 'short-password', 'password': '123'}, status=400)
admin.request('/api/v1/admin/users', 'POST', {'username': 'bad\nname', 'password': PASSWORD}, status=400)
check(admin.request('/api/v1/admin/users?search=' + name)['total'] == 1, 'user search')
check(admin.request('/api/v1/admin/users?search=%25')['total'] == 0, 'literal search does not broaden wildcard')
admin.request('/api/v1/admin/users?page=0', status=400)
admin.request('/api/v1/admin/users?status=unknown', status=400)
ordinary.login(name, status=401)
device_id = str(uuid.uuid4())


def sync_login(username, password=PASSWORD, device=device_id, status=200):
    return anonymous.request('/api/v1/auth/login', 'POST', {'username': username, 'password': password, 'deviceName': 'management test device', 'deviceId': device}, status=status)


tokens = sync_login(name)
owner = sync_login('alice')  # Same device UUID on a different account must remain isolated.
anonymous.request('/api/v1/admin/users', status=401, headers={'Authorization': 'Bearer ' + tokens['accessToken']})
anonymous.request('/api/v1/sync/changes?cursor=0', headers={'Authorization': 'Bearer ' + tokens['accessToken']})
devices = admin.request('/api/v1/admin/users/' + user_id + '/devices')
check(len(devices) == 1 and devices[0]['activeSessions'] == 1, 'device list belongs to selected user')
admin.request(f'/api/v1/admin/users/{user_id}/devices/{uuid.uuid4()}/session', 'DELETE', status=404)
admin.request(f'/api/v1/admin/users/{user_id}/devices/{device_id}/session', 'DELETE', status=204)
anonymous.request('/api/v1/account', status=401, headers={'Authorization': 'Bearer ' + tokens['accessToken']})
anonymous.request('/api/v1/account', headers={'Authorization': 'Bearer ' + owner['accessToken']})
tokens = sync_login(name)
admin.request(f'/api/v1/admin/users/{user_id}', 'PATCH', {'enabled': False, 'isAdmin': False})
sync_login(name, status=401)
anonymous.request('/api/v1/account', status=401, headers={'Authorization': 'Bearer ' + tokens['accessToken']})
anonymous.request('/api/v1/auth/refresh', 'POST', {'refreshToken': tokens['refreshToken']}, status=401)
check(admin.request('/api/v1/admin/users?status=disabled')['total'] >= 1, 'disabled user filter')
admin.request(f'/api/v1/admin/users/{user_id}', 'PATCH', {'enabled': True, 'isAdmin': False})
anonymous.request('/api/v1/account', status=401, headers={'Authorization': 'Bearer ' + tokens['accessToken']})
tokens = sync_login(name)
new_password = 'changed-test-password'
admin.request(f'/api/v1/admin/users/{user_id}/password', 'POST', {'password': new_password}, status=204)
sync_login(name, status=401)
anonymous.request('/api/v1/account', status=401, headers={'Authorization': 'Bearer ' + tokens['accessToken']})
tokens = sync_login(name, new_password)
check(tokens['accountId'] == user_id, 'password reset preserves account identity')
admin.request(f'/api/v1/admin/users/{user_id}/sessions', 'DELETE', status=204)
anonymous.request('/api/v1/account', status=401, headers={'Authorization': 'Bearer ' + tokens['accessToken']})
check(admin.request(f'/api/v1/admin/users/{user_id}/devices')[0]['activeSessions'] == 0, 'all-device revocation')

admin.request(f'/api/v1/admin/users/{user_id}', 'PATCH', {'enabled': True, 'isAdmin': True})
promoted = Client()
promoted.login(name, new_password)
promoted.request('/api/v1/admin/overview')
admin.request(f'/api/v1/admin/users/{user_id}', 'PATCH', {'enabled': True, 'isAdmin': False})
promoted.request('/api/v1/admin/users', status=401)
self_id = admin.request('/api/v1/admin/me')['accountId']
admin.request(f'/api/v1/admin/users/{self_id}', 'PATCH', {'enabled': False, 'isAdmin': True}, status=409)
admin.request(f'/api/v1/admin/users/{self_id}', 'PATCH', {'enabled': True, 'isAdmin': False}, status=409)
entries = admin.request('/api/v1/admin/audit')
check(any(entry['action'] == 'reset_password' and entry['target'] == name for entry in entries), 'management actions audited without passwords')
admin.request('/api/v1/admin/logout', 'POST', status=204)
admin.request('/api/v1/admin/me', status=401)

# Browser code ships in the real published application and is protected by CSP.
request = urllib.request.Request(BASE + '/admin/')
with urllib.request.urlopen(request) as response:
    check(response.status == 200 and b'login-form' in response.read(), 'console page served')
    check("frame-ancestors 'none'" in response.headers.get('Content-Security-Policy', ''), 'console CSP')
anonymous.request('/admin/admin.js')
anonymous.request('/admin/admin.css')
print(f'PASS management console, roles, CSRF, account lifecycle, device isolation: {checks} checks')
