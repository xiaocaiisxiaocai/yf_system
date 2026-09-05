# -*- coding: utf-8 -*-
"""后端全链路 E2E 测试：认证/组织/用户/角色/供应商/项目/轮次/上传/文件/留言/日志/工作台"""
import json, os, urllib.request, urllib.error, urllib.parse, uuid, sys

BASE = "http://127.0.0.1:8080/api/v1"
PASS, FAIL = 0, 0
ADMIN_PASSWORD = os.environ.get("YF_E2E_ADMIN_PASSWORD")
ADMIN_INITIAL_PASSWORD = os.environ.get("YF_E2E_ADMIN_INITIAL_PASSWORD")
ADMIN_NEW_PASSWORD = os.environ.get("YF_E2E_ADMIN_NEW_PASSWORD")

if not ADMIN_PASSWORD:
    print("缺少 YF_E2E_ADMIN_PASSWORD；测试不会读取或内置管理员密码。", file=sys.stderr)
    sys.exit(2)

def req(method, path, body=None, token=None, raw=None):
    url = BASE + path
    h = {"Content-Type": "application/json"}
    if token: h["Authorization"] = "Bearer " + token
    data = None
    if raw is not None:
        data = raw
        h["Content-Type"] = "application/octet-stream"
    elif body is not None:
        data = json.dumps(body).encode()
    r = urllib.request.Request(url, data=data, headers=h, method=method)
    try:
        with urllib.request.urlopen(r) as resp:
            ct = resp.headers.get("Content-Type", "")
            payload = resp.read()
            if "application/json" in ct:
                return resp.status, json.loads(payload), dict(resp.headers)
            return resp.status, payload, dict(resp.headers)
    except urllib.error.HTTPError as e:
        payload = e.read()
        try: return e.code, json.loads(payload), dict(e.headers)
        except Exception: return e.code, payload, dict(e.headers)

def check(name, cond, extra=""):
    global PASS, FAIL
    if cond: PASS += 1; print(f"  [PASS] {name}")
    else: FAIL += 1; print(f"  [FAIL] {name} :: {extra}")

def login(username, password, captcha_id=None, captcha_code=None):
    body = {"username": username, "password": password}
    if captcha_id: body["captchaId"] = captcha_id; body["captchaCode"] = captcha_code
    return req("POST", "/auth/login", body)[:2]

print("== 1. 登录与认证 ==")
s, r = login("admin", ADMIN_PASSWORD)
active_admin_password = ADMIN_PASSWORD
if s == 200:
    ADMIN = r["accessToken"]
    check("管理员登录", True)
else:
    if not ADMIN_INITIAL_PASSWORD or not ADMIN_NEW_PASSWORD:
        print("管理员登录失败；如需覆盖首次改密流程，请同时设置 YF_E2E_ADMIN_INITIAL_PASSWORD 和 YF_E2E_ADMIN_NEW_PASSWORD。", file=sys.stderr)
        sys.exit(2)
    s, r = login("admin", ADMIN_INITIAL_PASSWORD)
    check("管理员登录(初始密码)", s == 200 and r.get("accessToken"), f"got {s} {r}")
    ADMIN = r.get("accessToken")
    check("首次登录要求改密", r.get("mustChangePassword") == True, str(r.get("mustChangePassword")))
    s2, r2, _ = req("PUT", "/auth/password", {"oldPassword": ADMIN_INITIAL_PASSWORD, "newPassword": ADMIN_NEW_PASSWORD}, ADMIN)
    check("修改密码", s2 == 200, f"got {s2} {r2}")
    s, r = login("admin", ADMIN_NEW_PASSWORD)
    check("新密码重新登录", s == 200 and r.get("accessToken"), f"got {s} {r}")
    ADMIN = r["accessToken"]
    active_admin_password = ADMIN_NEW_PASSWORD
check("登录返回权限码", "user:manage" in r.get("permissions", []), str(r.get("permissions")))
check("登录返回菜单", "project:list" in r.get("menus", []), str(r.get("menus")))
s, r, _ = req("GET", "/auth/profile", token=ADMIN)
check("profile 返回用户+权限", s == 200 and r.get("user", {}).get("username") == "admin" and "role:manage" in r.get("permissions", []), f"got {s} {r}")

# 弱密码拒绝
s, r, _ = req("PUT", "/auth/password", {"oldPassword": active_admin_password, "newPassword": "short"}, ADMIN)
check("弱密码被拒(400)", s == 400, f"got {s} {r}")

print("== 2. 组织 / 用户 / 角色（M1） ==")
s, r, _ = req("POST", "/admin/departments", {"name": "项目部", "parentId": None, "sortNo": 1}, ADMIN)
check("创建部门", s == 200 and r.get("id"), f"got {s} {r}")
DEPT_ID = r.get("id")
s, r, _ = req("POST", "/admin/departments", {"name": "项目一组", "parentId": DEPT_ID, "sortNo": 1}, ADMIN)
SUB_DEPT = r.get("id")
check("创建子部门", s == 200 and SUB_DEPT, f"got {s} {r}")
# 循环校验：父部门挂到子部门下
s, r, _ = req("PUT", f"/admin/departments/{DEPT_ID}", {"name": "项目部", "parentId": SUB_DEPT, "sortNo": 1}, ADMIN)
check("部门循环引用被拒(400)", s == 400, f"got {s} {r}")
s, r, _ = req("GET", "/departments", token=ADMIN)
def find_node(nodes, nid):
    for n in nodes:
        if n.get("id") == nid: return n
        got = find_node(n.get("children") or [], nid)
        if got: return got
    return None
tree_ok = isinstance(r, list) and find_node(r, DEPT_ID) and (find_node(r, DEPT_ID).get("children") or [{}])[0].get("id") == SUB_DEPT
check("部门树结构", bool(tree_ok), f"got {s} {json.dumps(r, ensure_ascii=False)[:200]}")

s, r, _ = req("GET", "/admin/roles?page=1&pageSize=50", token=ADMIN)
check("角色列表", s == 200 and r.get("list"), f"got {s} {r}")
roles = {x["code"]: x for x in r["list"]}
PM_ROLE = roles.get("PROJECT_MANAGER", {}).get("id")
STAFF_ROLE = roles.get("STAFF", {}).get("id")
SUPPLIER_ROLE = roles.get("SUPPLIER", {}).get("id")
check("预置4角色", all(k in roles for k in ("ADMIN", "PROJECT_MANAGER", "STAFF", "SUPPLIER")), str(list(roles)))
check("角色含permissionIds", isinstance(roles.get("ADMIN", {}).get("permissionIds"), list), str(roles.get("ADMIN")))
s, r, _ = req("GET", "/admin/user-role-options", token=ADMIN)
check("内部用户角色选项排除供应商角色", s == 200 and all(x.get("code") != "SUPPLIER" for x in r), f"got {s} {r}")

s, r, _ = req("POST", "/admin/users", {
    "username": "pm_zhang", "password": "Pm@123456", "realName": "张项目",
    "email": "pm_zhang@example.com", "departmentId": DEPT_ID, "roleId": PM_ROLE}, ADMIN)
check("创建内部用户并保存部门/单角色", s == 200 and r.get("id") and r.get("departmentId") == DEPT_ID and r.get("roleId") == PM_ROLE, f"got {s} {r}")
PM_ID = r.get("id")
s, r, _ = req("POST", "/admin/users", {
    "username": "pm_zhang", "password": "Pm@123456", "realName": "重复", "email": "x@x.com"}, ADMIN)
check("重名用户被拒", s in (400, 409), f"got {s} {r}")

s, r, _ = req("PUT", f"/admin/users/{PM_ID}/roles", {"roleIds": [PM_ROLE, STAFF_ROLE]}, ADMIN)
check("用户绑定多个角色被拒(400)", s == 400 and "必须且只能" in r.get("message", ""), f"got {s} {r}")
s, r, _ = req("PUT", f"/admin/users/{PM_ID}/roles", {"roleIds": []}, ADMIN)
check("启用内部用户清空角色被拒(400)", s == 400, f"got {s} {r}")
s, r, _ = req("PUT", f"/admin/users/{PM_ID}", {"roleId": SUPPLIER_ROLE}, ADMIN)
check("内部用户绑定供应商角色被拒(400)", s == 400, f"got {s} {r}")
s, r, _ = req("PUT", f"/admin/users/{PM_ID}/roles", {"roleIds": [PM_ROLE]}, ADMIN)
check("用户绑定角色", s == 200, f"got {s} {r}")
s, r, _ = req("PUT", f"/admin/users/{PM_ID}", {"departmentId": None, "roleId": PM_ROLE}, ADMIN)
check("用户部门可以清空", s == 200 and r.get("departmentId") is None, f"got {s} {r}")
s, r, _ = req("PUT", f"/admin/users/{PM_ID}", {"departmentId": DEPT_ID, "roleId": PM_ROLE}, ADMIN)
check("用户部门可以恢复", s == 200 and r.get("departmentId") == DEPT_ID, f"got {s} {r}")

s, r, _ = req("GET", "/permissions", token=ADMIN)
check("权限树(8菜单+20操作)", s == 200 and isinstance(r, list) and len(r) == 28, f"got {s} len={len(r) if isinstance(r,list) else r}")
PERMS = {p["code"]: p["id"] for p in r}

s, r, _ = req("POST", "/admin/roles", {"code": "VIEWER", "name": "只读角色", "description": "仅查看项目"}, ADMIN)
check("创建自定义角色", s == 200 and r.get("id"), f"got {s} {r}")
VIEWER_ROLE = r.get("id")
s, r, _ = req("PUT", f"/admin/roles/{VIEWER_ROLE}/permissions",
          {"permissionIds": [PERMS["dashboard"], PERMS["project:list"], PERMS["file:download"], PERMS["file:preview"]]}, ADMIN)
check("角色授权", s == 200, f"got {s} {r}")
s, r, _ = req("GET", "/admin/roles?page=1&pageSize=50", token=ADMIN)
viewer = [x for x in r["list"] if x["code"] == "VIEWER"]
check("授权回读", viewer and set(viewer[0]["permissionIds"]) == {PERMS["dashboard"], PERMS["project:list"], PERMS["file:download"], PERMS["file:preview"]}, str(viewer))

s, pm_login = login("pm_zhang", "Pm@123456")
check("PM 登录", s == 200 and pm_login.get("accessToken"), f"got {s} {pm_login}")
PM = pm_login["accessToken"]
if pm_login.get("mustChangePassword"):
    s, r, _ = req("PUT", "/auth/password", {"oldPassword": "Pm@123456", "newPassword": "Pm@654321"}, PM)
    check("PM 首登强制改密", s == 200, f"got {s} {r}")
    s, pm_login = login("pm_zhang", "Pm@654321")
    check("PM 改密后重登", s == 200 and pm_login.get("accessToken"), f"got {s} {pm_login}")
    PM = pm_login["accessToken"]
check("PM 权限含 project:create", "project:create" in pm_login.get("permissions", []), str(pm_login.get("permissions")))
check("PM 菜单不含 system:config", "system:config" not in pm_login.get("menus", []), str(pm_login.get("menus")))

s, r, _ = req("GET", "/admin/users?page=1&pageSize=10", token=PM)
check("PM 访问用户管理被拒(403)", s == 403, f"got {s} {r}")

print("== 3. 供应商与账号（M1） ==")
s, r, _ = req("GET", "/admin/suppliers?page=1&pageSize=50&keyword=SUP-HY", token=ADMIN)
exist = [x for x in r.get("list", []) if x["code"] == "SUP-HY"]
if exist:
    SUP_ID = exist[0]["id"]
    check("创建供应商(复用已有)", True, "reuse SUP-HY")
else:
    s, r, _ = req("POST", "/admin/suppliers", {"name": "宏远精密制造有限公司", "code": "SUP-HY", "contactName": "李工", "contactPhone": "13800000001", "contactEmail": "hongyuan@supplier.com"}, ADMIN)
    check("创建供应商", s == 200 and r.get("id"), f"got {s} {r}")
    SUP_ID = r.get("id")
s, r, _ = req("POST", "/admin/suppliers", {"name": "宏远精密制造有限公司", "code": "SUP-HY"}, ADMIN)
check("供应商编码重复被拒", s in (400, 409), f"got {s} {r}")
s, r, _ = req("GET", f"/admin/suppliers/{SUP_ID}/accounts", token=ADMIN)
acct = [x for x in (r if isinstance(r, list) else r.get("list", [])) if x["username"] == "hy_li"]
if acct:
    SUP_UID = acct[0]["id"]
    check("创建供应商账号(复用已有)", True, "reuse hy_li")
else:
    s, r, _ = req("POST", f"/admin/suppliers/{SUP_ID}/accounts", {
        "username": "hy_li", "password": "Hy@123456", "realName": "李工", "email": "li@hongyuan.com"}, ADMIN)
    check("创建供应商账号", s == 200 and r.get("id"), f"got {s} {r}")
    SUP_UID = r.get("id")

s, r, _ = req("GET", "/admin/suppliers?page=1&pageSize=50&keyword=SUP-LH", token=ADMIN)
exist2 = [x for x in r.get("list", []) if x["code"] == "SUP-LH"]
if exist2:
    SUP2_ID = exist2[0]["id"]
else:
    s, r, _ = req("POST", "/admin/suppliers", {"name": "蓝海电子科技", "code": "SUP-LH", "contactName": "王经理"}, ADMIN)
    SUP2_ID = r.get("id")
s, r, _ = req("GET", f"/admin/suppliers/{SUP2_ID}/accounts", token=ADMIN)
acct2 = [x for x in (r if isinstance(r, list) else r.get("list", [])) if x["username"] == "lh_wang"]
if not acct2:
    s, r, _ = req("POST", f"/admin/suppliers/{SUP2_ID}/accounts", {
        "username": "lh_wang", "password": "Lh@123456", "realName": "王经理", "email": "wang@lanhai.com"}, ADMIN)
    check("创建第二供应商+账号", s == 200 and r.get("id"), f"got {s} {r}")
else:
    check("创建第二供应商+账号(复用)", True, "reuse lh_wang")

s, r, _ = req("GET", f"/admin/suppliers/{SUP_ID}/accounts", token=ADMIN)
check("供应商账号列表", s == 200 and isinstance(r, list) and r[0].get("username") == "hy_li", f"got {s} {r}")

s, sup_login = login("hy_li", "Hy@654321")
if s != 200 or not sup_login.get("accessToken"):
    s, sup_login = login("hy_li", "Hy@123456")
check("供应商登录", s == 200 and sup_login.get("accessToken"), f"got {s} {sup_login}")
SUP = sup_login["accessToken"]
HY_PW = "Hy@654321" if not sup_login.get("mustChangePassword") else "Hy@123456"
if sup_login.get("mustChangePassword"):
    s, r, _ = req("GET", "/projects", token=SUP)
    check("未改密访问业务接口被拒(40303)", s == 403 and r.get("code") == 40303, f"got {s} {r}")
    s, r, _ = req("PUT", "/auth/password", {"oldPassword": HY_PW, "newPassword": "Hy@654321"}, SUP)
    check("供应商首登强制改密", s == 200, f"got {s} {r}")
    s, sup_login = login("hy_li", "Hy@654321")
    check("供应商改密后重登", s == 200 and sup_login.get("accessToken") and not sup_login.get("mustChangePassword"), f"got {s} {sup_login}")
    SUP = sup_login["accessToken"]
    HY_PW = "Hy@654321"
SUP_UID_FROM_LOGIN = sup_login["user"]["id"]
check("供应商类型 SUPPLIER", sup_login["user"]["userType"] == "SUPPLIER", str(sup_login["user"]))
check("供应商菜单仅基础项", set(sup_login.get("menus", [])) == {"dashboard", "project:list"}, str(sup_login.get("menus")))
# 前一轮运行可能已触发验证码门槛：管理员重置密码会清零失败计数（同时验证该接口）
s, r, _ = req("GET", f"/admin/suppliers/{SUP2_ID}/accounts", token=ADMIN)
LH_UID = [x for x in (r if isinstance(r, list) else r.get("list", [])) if x["username"] == "lh_wang"][0]["id"]
s, r, _ = req("PUT", f"/admin/supplier-accounts/{LH_UID}/password", {"newPassword": "Lh@123456"}, ADMIN)
check("重置供应商账号密码(清除验证码状态)", s == 200, f"got {s} {r}")
s, sup2_login = login("lh_wang", "Lh@123456")
check("第二供应商登录", s == 200 and sup2_login.get("accessToken"), f"got {s} {sup2_login}")
SUP2 = sup2_login["accessToken"]
LH_PW = "Lh@123456"
if sup2_login.get("mustChangePassword"):
    s, r, _ = req("PUT", "/auth/password", {"oldPassword": "Lh@123456", "newPassword": "Lh@654321"}, SUP2)
    check("第二供应商首登改密", s == 200, f"got {s} {r}")
    s, sup2_login = login("lh_wang", "Lh@654321")
    SUP2 = sup2_login.get("accessToken", "")
    LH_PW = "Lh@654321"

print("== 4. 项目 / 轮次（M2） ==")
s, r, _ = req("POST", "/projects", {"code": "PRJ-HX2600", "name": "HX-2600 壳体打样", "supplierId": SUP_ID, "description": "铝合金壳体 CNC 打样"}, PM)
check("PM 创建项目", s == 200 and r.get("id"), f"got {s} {r}")
PROJ_ID = r.get("id")
check("新项目为草稿", r.get("status") == "DRAFT", str(r))
s, r, _ = req("POST", "/projects", {"code": "PRJ-LD100", "name": "LD-100 电源适配", "supplierId": SUP2_ID}, PM)
PROJ2_ID = r.get("id")
check("创建第二项目", s == 200 and PROJ2_ID, f"got {s} {r}")
s, r, _ = req("POST", "/projects", {"code": "PRJ-HX2600", "name": "重复编码", "supplierId": SUP_ID}, PM)
check("项目编码重复被拒", s in (400, 409), f"got {s} {r}")

s, r, _ = req("GET", f"/projects/{PROJ_ID}/members", token=PM)
pm_uid = pm_login["user"]["id"]
check("创建者自动成为成员", s == 200 and any(m.get("userId") == pm_uid or m.get("id") == pm_uid for m in (r if isinstance(r, list) else [])), f"got {s} {r}")
s, r, _ = req("PUT", f"/projects/{PROJ_ID}/members", {"userIds": [pm_uid]}, PM)
check("设置项目成员", s == 200, f"got {s} {r}")

# 草稿项目不可建轮次
s, r, _ = req("POST", f"/projects/{PROJ_ID}/rounds", {"title": "早", "confirmSide": "SUPPLIER"}, PM)
check("草稿项目建轮次被拒(409)", s == 409, f"got {s} {r}")
s, r, _ = req("PUT", f"/projects/{PROJ_ID}/status", {"status": "IN_PROGRESS"}, PM)
check("项目开工", s == 200 and r.get("status") == "IN_PROGRESS", f"got {s} {r}")
s, r, _ = req("PUT", f"/projects/{PROJ_ID}/status", {"status": "DRAFT"}, PM)
check("进行中不可退回草稿(409/400)", s in (400, 409), f"got {s} {r}")

# 数据隔离
s, r, _ = req("GET", "/projects?page=1&pageSize=20", token=SUP)
ids = [p["id"] for p in r.get("list", [])]
check("供应商仅见本项目", s == 200 and PROJ_ID in ids and PROJ2_ID not in ids, f"got {s} ids={ids}")
s, r, _ = req("GET", f"/projects/{PROJ2_ID}", token=SUP)
check("越权看他供应商项目(40302)", s == 403 and r.get("code") == 40302, f"got {s} {r}")
s, r, _ = req("GET", f"/projects/{PROJ_ID}/members", token=SUP2)
check("供应商2访问项目1成员被拒", s == 403, f"got {s} {r}")

s, r, _ = req("POST", f"/projects/{PROJ_ID}/rounds", {"title": "首版图纸评审", "confirmSide": "SUPPLIER", "remark": "请确认图纸"}, PM)
check("创建轮次(供应商确认)", s == 200 and r.get("roundNo") == 1, f"got {s} {r}")
R1 = r.get("id")
s, r, _ = req("POST", f"/projects/{PROJ_ID}/rounds", {"title": "工艺确认", "confirmSide": "COMPANY"}, PM)
check("第二轮次 roundNo=2", s == 200 and r.get("roundNo") == 2, f"got {s} {r}")
R2 = r.get("id")
s, r, _ = req("POST", f"/projects/{PROJ_ID}/rounds", {"title": "供应商自建", "confirmSide": "COMPANY"}, SUP)
check("供应商建轮次被拒(403)", s == 403, f"got {s} {r}")
s, r, _ = req("POST", f"/projects/{PROJ_ID}/rounds", {"title": "坏边", "confirmSide": "XXX"}, PM)
check("非法 confirmSide 被拒(400)", s == 400, f"got {s} {r}")

s, r, _ = req("POST", f"/rounds/{R1}/reject", {}, SUP)
check("无理由驳回被拒(400)", s == 400, f"got {s} {r}")
s, r, _ = req("POST", f"/rounds/{R1}/reject", {"reason": "图纸缺少公差标注"}, SUP)
check("供应商驳回轮次1", s == 200 and r.get("status") == "REJECTED", f"got {s} {r}")
check("驳回原因落库", r.get("rejectReason") == "图纸缺少公差标注", str(r))
s, r, _ = req("POST", f"/rounds/{R2}/confirm", {}, PM)
check("公司确认轮次2", s == 200 and r.get("status") == "CONFIRMED", f"got {s} {r}")
check("确认人记录", r.get("decidedBy") == pm_uid, str(r.get("decidedBy")))
s, r, _ = req("POST", f"/rounds/{R2}/confirm", {}, SUP)
check("错方确认被拒(403/409)", s in (403, 409), f"got {s} {r}")
s, r, _ = req("POST", f"/rounds/{R1}/confirm", {}, SUP)
check("终态轮次不可再确认(409)", s == 409, f"got {s} {r}")
s, r, _ = req("GET", f"/rounds/{R1}", token=PM)
check("轮次历史(创建+驳回2条)", s == 200 and len(r.get("logs", [])) == 2, f"got {s} {json.dumps(r, ensure_ascii=False)[:300]}")
check("历史含操作人", r.get("logs", [{}])[-1].get("operatorName") == "李工", str(r.get("logs")))

print("== 5. 分片上传 / 文件（M3） ==")
# 调小分片到 1MB 以测多片
s, r, _ = req("PUT", "/admin/system/configs", {"items": [{"key": "upload.chunk_size", "value": "1048576"}]}, ADMIN)
check("调整分片大小参数", s == 200, f"got {s} {r}")

# 轮次3：PENDING 中可上传
s, r, _ = req("POST", f"/projects/{PROJ_ID}/rounds", {"title": "返修确认", "confirmSide": "SUPPLIER"}, PM)
R3 = r.get("id")

content = (b"YF-E2E-TEST-DATA-" + uuid.uuid4().bytes) * 50000  # ~1.65MB → 2 片
s, r, _ = req("POST", "/uploads/init", {"projectId": PROJ_ID, "roundId": R3, "fileName": "large-upload-test.zip", "fileSize": len(content)}, PM)
check("上传初始化(2片)", s == 200 and r.get("sessionId") and r.get("totalChunks") == 2, f"got {s} {r}")
SID = r["sessionId"]; CS = r["chunkSize"]
chunks = [content[i:i+CS] for i in range(0, len(content), CS)]
s, r, _ = req("PUT", f"/uploads/{SID}/chunks/0", token=PM, raw=chunks[0])
check("上传分片0", s == 200, f"got {s} {r}")
s, r, _ = req("PUT", f"/uploads/{SID}/chunks/0", token=PM, raw=chunks[0])
check("重复分片幂等", s == 200, f"got {s} {r}")
s, r, _ = req("GET", f"/uploads/{SID}", token=PM)
check("断点状态 uploadedChunks=[0]", s == 200 and r.get("uploadedChunks") == [0], f"got {s} {r}")
# 缺片时合并被拒
s, r, _ = req("POST", f"/uploads/{SID}/merge", {}, PM)
check("分片不全合并被拒(400)", s == 400, f"got {s} {r}")
s, r, _ = req("PUT", f"/uploads/{SID}/chunks/1", token=PM, raw=chunks[1])
check("上传分片1", s == 200, f"got {s} {r}")
# 越界序号
s, r, _ = req("PUT", f"/uploads/{SID}/chunks/5", token=PM, raw=b"x", )
check("分片越界被拒(400)", s == 400, f"got {s} {r}")
# 他人会话不可写
s, r, _ = req("PUT", f"/uploads/{SID}/chunks/0", token=SUP, raw=chunks[0])
check("非本人会话上传被拒(403)", s == 403, f"got {s} {r}")
s, r, _ = req("POST", f"/uploads/{SID}/merge", {}, PM)
check("合并分片得文件", s == 200 and r.get("id"), f"got {s} {r}")
FILE_ID = r["id"]
check("方向 C2S", r.get("direction") == "C2S", str(r))
check("sha256 落库", len(r.get("sha256", "")) == 64, str(r.get("sha256")))
# merge 幂等
s, r, _ = req("POST", f"/uploads/{SID}/merge", {}, PM)
check("重复合并幂等", s == 200 and r.get("id") == FILE_ID, f"got {s} {r}")

# 供应商上传（S2C）
content2 = b"supplier-feedback-" * 1000
s, r, _ = req("POST", "/uploads/init", {"projectId": PROJ_ID, "roundId": R3, "fileName": "供应商反馈.pdf", "fileSize": len(content2)}, SUP)
SID2 = r["sessionId"]
s, r, _ = req("PUT", f"/uploads/{SID2}/chunks/0", token=SUP, raw=content2)
s, r, _ = req("POST", f"/uploads/{SID2}/merge", {}, SUP)
check("供应商上传合并", s == 200 and r.get("id"), f"got {s} {r}")
FILE2_ID = r["id"]
check("方向 S2C", r.get("direction") == "S2C", str(r))

# 禁止扩展名
s, r, _ = req("POST", "/uploads/init", {"projectId": PROJ_ID, "roundId": R3, "fileName": "evil.exe", "fileSize": 100}, PM)
check("非法扩展名被拒(400)", s == 400, f"got {s} {r}")

s, r, _ = req("GET", f"/projects/{PROJ_ID}/files?direction=C2S", token=PM)
check("文件列表方向过滤", s == 200 and r.get("total") == 1 and r["list"][0]["originalName"] == "large-upload-test.zip", f"got {s} {r}")
s, r, _ = req("GET", f"/projects/{PROJ_ID}/files?keyword=" + urllib.parse.quote("反馈"), token=PM)
check("文件列表关键字过滤", s == 200 and r.get("total") == 1, f"got {s} {r}")

s, data, hdr = req("GET", f"/files/{FILE_ID}/download", token=PM)
check("下载内容一致", s == 200 and data == content, f"got {s} len={len(data) if isinstance(data,bytes) else data}")
cd = hdr.get("Content-Disposition") or hdr.get("content-disposition") or ""
check("下载头 filename*", "filename*=UTF-8''" in cd and "attachment" in cd, cd)
s, data2, _ = req("GET", f"/files/{FILE_ID}/download", token=SUP)
check("供应商下载本项目文件", s == 200 and data2 == content, f"got {s}")
s, r, _ = req("GET", f"/files/{FILE_ID}/download", token=SUP2)
check("他供应商下载被拒(40302)", s == 403 and r.get("code") == 40302, f"got {s} {r}")
s, data3, hdr = req("GET", f"/files/{FILE_ID}/content", token=SUP)
cd = hdr.get("Content-Disposition") or hdr.get("content-disposition") or ""
check("内联预览流", s == 200 and data3 == content and "inline" in cd, f"got {s} cd={cd}")
s, zdata, hdr = req("POST", "/files/batch-download", {"ids": [FILE_ID, FILE2_ID]}, PM)
check("批量打包 zip", s == 200 and isinstance(zdata, bytes) and zdata[:2] == b"PK", f"got {s}")

# 已确认轮次上传被拒
s, r, _ = req("POST", "/uploads/init", {"projectId": PROJ_ID, "roundId": R2, "fileName": "迟到.txt", "fileSize": 10}, PM)
check("已确认轮次上传被拒(409)", s == 409, f"got {s} {r}")
# 供应商2 在他人项目上传被拒
s, r, _ = req("POST", "/uploads/init", {"projectId": PROJ_ID, "roundId": R3, "fileName": "x.txt", "fileSize": 10}, SUP2)
check("越权项目上传被拒(403)", s == 403, f"got {s} {r}")

print("== 6. 留言与已读（M2） ==")
s, r, _ = req("POST", f"/projects/{PROJ_ID}/messages", {"roundId": R3, "content": "请按最新公差表返修，详见附件。"}, PM)
check("公司留言(轮次级)", s == 200 and r.get("id"), f"got {s} {r}")
MSG1 = r["id"]
check("发送者不计已读", r.get("readCount") == 0 and r.get("totalCount") == 1, str(r))
s, r, _ = req("POST", f"/projects/{PROJ_ID}/messages", {"content": "收到，本周五前交付。"}, SUP)
check("供应商留言(项目级)", s == 200 and r.get("id"), f"got {s} {r}")
MSG2 = r["id"]
# 已确认轮次留言被拒
s, r, _ = req("POST", f"/projects/{PROJ_ID}/messages", {"roundId": R2, "content": "晚了"}, PM)
check("已确认轮次留言被拒(409)", s == 409, f"got {s} {r}")
# 空留言
s, r, _ = req("POST", f"/projects/{PROJ_ID}/messages", {"content": "   "}, PM)
check("空留言被拒(400)", s == 400, f"got {s} {r}")

s, r, _ = req("GET", f"/projects/{PROJ_ID}/messages?page=1&pageSize=20", token=SUP)
check("留言流(时间倒序)", s == 200 and r.get("total") == 2 and r["list"][0]["id"] == MSG2, f"got {s} {r}")
m1 = [m for m in r["list"] if m["id"] == MSG1][0]
check("供应商视角 MSG1 未读", m1.get("readByMe") == False, str(m1))
check("留言带发送人", m1.get("senderName") == "张项目" and m1.get("senderType") == "INTERNAL", str(m1))
# 轮次过滤
s, r, _ = req("GET", f"/projects/{PROJ_ID}/messages?roundId={R3}", token=SUP)
check("按轮次过滤留言", s == 200 and r.get("total") == 1, f"got {s} {r}")
s, r, _ = req("GET", f"/projects/{PROJ_ID}/messages?roundId=0", token=SUP)
check("仅项目级留言", s == 200 and r.get("total") == 1 and r["list"][0]["id"] == MSG2, f"got {s} {r}")

s, r, _ = req("POST", "/messages/read", {"ids": [MSG1]}, SUP)
check("标记已读", s == 200, f"got {s} {r}")
s, r, _ = req("POST", "/messages/read", {"ids": [MSG1]}, SUP)
check("重复已读幂等", s == 200, f"got {s} {r}")
s, r, _ = req("GET", f"/messages/{MSG1}/reads", token=PM)
check("已读回执 1/1", s == 200 and len(r.get("readers", [])) == 1 and len(r.get("unread", [])) == 0, f"got {s} {r}")
check("回执名单含李工", any(x.get("realName") == "李工" and x.get("readAt") for x in r.get("readers", [])), str(r))
# 供应商2 不可见他人项目留言
s, r, _ = req("GET", f"/projects/{PROJ_ID}/messages", token=SUP2)
check("供应商2留言流被拒(40302)", s == 403 and r.get("code") == 40302, f"got {s} {r}")

print("== 7. 日志 / 系统参数 / 工作台（M4） ==")
s, r, _ = req("GET", "/admin/audit-logs?page=1&pageSize=50&action=FILE_DOWNLOAD", token=ADMIN)
check("日志按动作过滤", s == 200 and r.get("total", 0) >= 2, f"got {s} {r}")
s, r, _ = req("GET", "/admin/audit-logs?page=1&pageSize=50&username=hy_li", token=ADMIN)
check("日志按用户过滤", s == 200 and r.get("total", 0) >= 1, f"got {s} {r}")
check("日志含关键动作", any(l.get("action") == "ROUND_REJECT" for l in r.get("list", [])), str(r.get("list", [])))
s, r, _ = req("GET", "/admin/audit-logs?page=1&pageSize=50", token=ADMIN)
actions = {l.get("action") for l in r.get("list", [])}
check("日志覆盖关键操作", {"LOGIN", "ROUND_REJECT", "ROUND_CONFIRM", "FILE_DOWNLOAD", "MESSAGE_CREATE"} & actions == {"LOGIN", "ROUND_REJECT", "ROUND_CONFIRM", "FILE_DOWNLOAD", "MESSAGE_CREATE"}, str(actions))
s, r, _ = req("GET", "/admin/audit-logs", token=PM)
check("PM 查日志被拒(403)", s == 403, f"got {s} {r}")

s, r, _ = req("GET", "/admin/system/configs", token=ADMIN)
check("系统参数列表", s == 200 and isinstance(r, list) and len(r) >= 4, f"got {s} {r}")
cfgmap = {c["key"]: c["value"] for c in r}
check("分片参数已更新", cfgmap.get("upload.chunk_size") == "1048576", str(cfgmap))
s, r, _ = req("PUT", "/admin/system/configs", {"items": [{"key": "notify.enabled", "value": "true"}]}, ADMIN)
check("更新系统参数", s == 200, f"got {s} {r}")
s, r, _ = req("PUT", "/admin/system/configs", {"items": [{"key": "no.such.key", "value": "1"}]}, ADMIN)
check("未知参数被拒(400)", s == 400, f"got {s} {r}")
s, r, _ = req("GET", "/admin/system/storage", token=ADMIN)
check("磁盘状态", s == 200 and ("totalBytes" in r or "total" in json.dumps(r)), f"got {s} {r}")

s, r, _ = req("GET", "/dashboard/summary", token=PM)
check("PM 工作台", s == 200 and r.get("projectCount") >= 2 and r.get("unreadMessages") == 1, f"got {s} {r}")
check("工作台近期留言", len(r.get("recentMessages", [])) >= 1, str(r))
s, r, _ = req("GET", "/dashboard/summary", token=SUP)
check("供应商工作台(1项目/1待确认)", s == 200 and r.get("projectCount") == 1 and r.get("pendingRounds") == 1, f"got {s} {r}")
s, r, _ = req("GET", "/dashboard/summary", token=SUP2)
check("供应商2工作台(1项目0待办)", s == 200 and r.get("projectCount") == 1 and r.get("pendingRounds") == 0, f"got {s} {r}")

print("== 8. 禁用 / 验证码 / 锁定 ==")
s, r, _ = req("PUT", f"/admin/supplier-accounts/{SUP_UID}/status", {"status": "DISABLED"}, ADMIN)
check("禁用供应商账号", s == 200, f"got {s} {r}")
s, r = login("hy_li", HY_PW)
check("禁用账号无法登录(401)", s == 401, f"got {s} {r}")
s, r, _ = req("GET", "/auth/profile", token=SUP)
check("禁用后旧token立即失效(401)", s == 401, f"got {s} {r}")
s, r, _ = req("PUT", f"/admin/supplier-accounts/{SUP_UID}/status", {"status": "ACTIVE"}, ADMIN)
check("恢复账号", s == 200, f"got {s} {r}")
s, sup_login = login("hy_li", HY_PW)
check("恢复后可登录", s == 200 and sup_login.get("accessToken"), f"got {s} {r}")

# 验证码：lh_wang 连错 3 次（其 token 已在上文拿到，不再影响后续）
for i in range(3):
    login("lh_wang", "wrong-pass")
s, r = login("lh_wang", LH_PW)
check("3次失败后需验证码(428)", s == 428 and r.get("code") == 42801, f"got {s} {r}")
s, cap = req("GET", "/auth/captcha")[:2]
check("验证码接口返回SVG", s == 200 and cap.get("captchaId") and "<svg" in cap.get("svg", ""), f"got {s} {str(cap)[:120]}")
s, r = login("lh_wang", LH_PW, cap["captchaId"], "0000")
check("错误验证码仍拒(428)", s == 428, f"got {s} {r}")

# 未认证访问
s, r, _ = req("GET", "/projects")
check("无token访问被拒(401)", s == 401, f"got {s} {r}")
s, r, _ = req("GET", "/projects", token="bad-token")
check("伪造token被拒(401)", s == 401, f"got {s} {r}")

print()
print(f"==== 结果: {PASS} 通过, {FAIL} 失败 ====")
sys.exit(1 if FAIL else 0)
