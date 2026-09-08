"""Authorized live acceptance against the configured local business database.

Creates only uniquely marked fixtures; credentials stay in an ignored local state file.
Run through .runlogs/run_live.py to recover this workstation's authorized credentials.
"""
from pathlib import Path
import concurrent.futures, hashlib, io, json, os, secrets, sys, threading, time
import urllib.request, urllib.error, urllib.parse, zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.runlogs'))
from live_context import db
BASE = 'http://127.0.0.1:8080/api/v1'
STATE = ROOT / '.runlogs/live-fixtures.private.json'
REPORT = ROOT / '.runlogs/live-api-results.json'
results = []

def request(method, path, body=None, token=None, raw=None):
    headers = {'Content-Type': 'application/json'}
    if token: headers['Authorization'] = 'Bearer ' + token
    data = json.dumps(body).encode() if body is not None else None
    if raw is not None: data = raw; headers['Content-Type'] = 'application/octet-stream'
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try: response = urllib.request.urlopen(req, timeout=25)
    except urllib.error.HTTPError as error: response = error
    with response:
        payload = response.read()
        if 'application/json' in response.headers.get('Content-Type', ''): payload = json.loads(payload)
        return response.status, payload

def check(name, condition, detail=None):
    result = {'name': name, 'passed': bool(condition)}
    if detail is not None: result['detail'] = detail
    results.append(result)
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    REPORT.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')

def ok(method, path, body=None, token=None, raw=None):
    status, value = request(method, path, body, token, raw)
    if status != 200: raise RuntimeError(f'{method} {path} returned HTTP {status}')
    return value

def login(user):
    return ok('POST', '/auth/login', {'employeeNo': user['employeeNo'], 'password': user['password']})['accessToken']

def setup():
    admin = {'employeeNo': 'admin', 'password': os.environ['YF_E2E_ADMIN_PASSWORD']}
    token = login(admin)
    marker = 'LIVE' + time.strftime('%m%d%H%M%S')
    sink = os.environ['YF_E2E_RECIPIENT']
    state = {'marker': marker, 'admin': admin, 'users': {}, 'projects': [], 'supplier_ids': []}
    roles = {row['name']: row['id'] for row in ok('GET', '/admin/roles?pageSize=100', token=token)['list']}
    for role, suffix in [('项目管理员', 'pm'), ('内部成员', 'member')]:
        user = {'employeeNo': marker.lower() + suffix, 'password': secrets.token_urlsafe(18) + 'A9!', 'realName': marker + suffix, 'email': sink, 'roleId': roles[role]}
        user['id'] = ok('POST', '/admin/users', user, token)['id']
        state['users'][suffix] = user
    for suffix in ['supplier', 'outside']:
        supplier = ok('POST', '/admin/suppliers', {'name': marker + suffix}, token)
        state['supplier_ids'].append(supplier['id'])
        user = {'employeeNo': marker.lower() + suffix, 'password': secrets.token_urlsafe(18) + 'A9!', 'realName': marker + suffix, 'email': sink}
        user['id'] = ok('POST', f"/admin/suppliers/{supplier['id']}/accounts", user, token)['id']
        state['users'][suffix] = user
    for user in state['users'].values():
        auth = ok('POST', '/auth/login', {'employeeNo': user['employeeNo'], 'password': user['password']})
        if auth.get('mustChangePassword'):
            password = secrets.token_urlsafe(18) + 'A9!'
            ok('PUT', '/auth/password', {'oldPassword': user['password'], 'newPassword': password}, auth['accessToken'])
            user['password'] = password
    STATE.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding='utf-8')
    check('真实业务库专属账号与供应商创建、首次改密', True)
    return state

def project(state, token, label):
    p = ok('POST', '/projects', {'name': state['marker'] + label, 'supplierId': state['supplier_ids'][0]}, token)
    ok('PUT', f"/projects/{p['id']}/status", {'status': 'IN_PROGRESS'}, token)
    ok('PUT', f"/projects/{p['id']}/members", {'userIds': [state['users']['pm']['id'], state['users']['member']['id']]}, token)
    state['projects'].append(p['id']); STATE.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding='utf-8')
    return p['id']

def round_create(pid, token, title='验收轮次', side='COMPANY'):
    return ok('POST', f'/projects/{pid}/rounds', {'title': title, 'confirmSide': side}, token)['id']

def init(pid, rid, token, content=b'test', name='acceptance.pdf', digest=True):
    payload = {'projectId': pid, 'roundId': rid, 'fileName': name, 'fileSize': len(content)}
    if digest: payload['fileMd5'] = hashlib.md5(content).hexdigest()
    return ok('POST', '/uploads/init', payload, token)

def upload(pid, rid, token, content=b'test', name='acceptance.pdf'):
    session = init(pid, rid, token, content, name)
    sid, size = session['sessionId'], session['chunkSize']
    for i in range(session['totalChunks']): ok('PUT', f'/uploads/{sid}/chunks/{i}', token=token, raw=content[i*size:(i+1)*size])
    return ok('POST', f'/uploads/{sid}/merge', {}, token)

def parallel(*operations):
    barrier = threading.Barrier(len(operations))
    def run(operation): barrier.wait(); return operation()
    with concurrent.futures.ThreadPoolExecutor(max_workers=len(operations)) as pool:
        return list(pool.map(run, operations))

def wait_for_db_wait(blocker_id, timeout=20, future=None):
    deadline = time.monotonic() + timeout
    with db() as connection:
        while time.monotonic() < deadline:
            with connection.cursor() as cursor:
                cursor.execute("SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX b ON b.trx_id=w.blocking_trx_id WHERE b.trx_mysql_thread_id=%s", (blocker_id,))
                if cursor.fetchone()['n']: return
            if future is not None and future.done():
                raise AssertionError('请求在到达锁屏障前结束，HTTP ' + str(future.result()[0]))
            # MySQL 5.7 refreshes this shared cache only after >100ms since its
            # last read. Faster polling can indefinitely preserve an empty snapshot.
            time.sleep(.25)
    raise AssertionError('未观察到数据库锁等待，测试屏障未成立')

def concurrency(state):
    pm, member = login(state['users']['pm']), login(state['users']['member'])
    pid = project(state, pm, '并发')
    rid = round_create(pid, pm)
    session = init(pid, rid, member); sid = session['sessionId']
    connection = db(); connection.begin()
    try:
        with connection.cursor() as cursor: cursor.execute('SELECT id FROM upload_sessions WHERE id=%s FOR UPDATE', (sid,))
        with concurrent.futures.ThreadPoolExecutor() as pool:
            future = pool.submit(request, 'DELETE', f'/uploads/{sid}', None, member)
            try: wait_for_db_wait(connection.thread_id(), future=future)
            except Exception:
                connection.rollback()
                raise
            with connection.cursor() as cursor: cursor.execute("UPDATE upload_sessions SET status='MERGING' WHERE id=%s", (sid,))
            connection.commit()
            status, _ = future.result()
        with connection.cursor() as cursor:
            cursor.execute('SELECT status FROM upload_sessions WHERE id=%s', (sid,)); current = cursor.fetchone()['status']
        check('取消请求等待锁期间进入合并，取消必须拒绝且不得覆盖状态', status == 409 and current == 'MERGING', {'http': status, 'status': current})
    finally:
        connection.rollback()
        with connection.cursor() as cursor: cursor.execute("UPDATE upload_sessions SET status='UPLOADING' WHERE id=%s AND status='MERGING'", (sid,))
        connection.close()

    pid2 = project(state, pm, '建轮次屏障')
    connection = db(); connection.begin()
    try:
        with connection.cursor() as cursor: cursor.execute('SELECT id FROM projects WHERE id=%s FOR UPDATE', (pid2,))
        with concurrent.futures.ThreadPoolExecutor() as pool:
            future = pool.submit(request, 'POST', f'/projects/{pid2}/rounds', {'title': '不应创建', 'confirmSide': 'COMPANY'}, pm)
            wait_for_db_wait(connection.thread_id())
            with connection.cursor() as cursor: cursor.execute("UPDATE projects SET status='COMPLETED' WHERE id=%s", (pid2,))
            connection.commit()
            status, _ = future.result()
        with connection.cursor() as cursor:
            cursor.execute('SELECT COUNT(*) AS n FROM rounds WHERE project_id=%s', (pid2,)); count = cursor.fetchone()['n']
        check('建轮次等待锁期间项目完成，不得继续插入轮次', status == 409 and count == 0, {'http': status, 'rounds': count})
    finally: connection.rollback(); connection.close()

    rid2 = round_create(pid, pm, '并发合并')
    session = init(pid, rid2, member); sid = session['sessionId']
    ok('PUT', f'/uploads/{sid}/chunks/0', token=member, raw=b'test')
    outcomes = parallel(*[lambda: request('POST', f'/uploads/{sid}/merge', {}, member) for _ in range(4)])
    with db() as connection, connection.cursor() as cursor:
        cursor.execute('SELECT COUNT(*) AS n FROM files WHERE round_id=%s', (rid2,)); count = cursor.fetchone()['n']
    check('四请求并发合并仅产生一个文件', count == 1 and all(s in (200,409) for s,_ in outcomes), {'http': [s for s,_ in outcomes], 'files': count})

    rid3 = round_create(pid, pm, '确认上传竞争')
    session = init(pid, rid3, member); sid = session['sessionId']
    ok('PUT', f'/uploads/{sid}/chunks/0', token=member, raw=b'test')
    outcomes = parallel(lambda: request('POST', f'/uploads/{sid}/merge', {}, member), lambda: request('POST', f'/rounds/{rid3}/confirm', {}, pm))
    check('确认轮次与上传竞争无死锁或服务器错误', all(s in (200,409) for s,_ in outcomes), {'http': [s for s,_ in outcomes]})

def lock_order(state):
    pm = login(state['users']['pm'])
    pid = project(state, pm, '锁顺序')
    rid = round_create(pid, pm)
    connection = db(); connection.begin(); lock_error = None
    try:
        with connection.cursor() as cursor:
            cursor.execute('SET SESSION innodb_lock_wait_timeout=3')
            cursor.execute('SELECT id FROM projects WHERE id=%s FOR UPDATE', (pid,))
        with concurrent.futures.ThreadPoolExecutor() as pool:
            future = pool.submit(request, 'POST', f'/rounds/{rid}/confirm', {}, pm)
            try: wait_for_db_wait(connection.thread_id(), future=future)
            except Exception:
                connection.rollback()
                raise
            try:
                with connection.cursor() as cursor: cursor.execute('SELECT id FROM rounds WHERE id=%s FOR UPDATE', (rid,))
            except Exception as error: lock_error = type(error).__name__
            connection.commit()
            status, _ = future.result()
        check('轮次流转遵循项目先于轮次的锁顺序', lock_error is None and status == 200, {'http': status, 'lock_error': lock_error})
    finally: connection.rollback(); connection.close()

def business(state):
    admin, pm, member, supplier, outside = (login(state['admin']), *[login(state['users'][key]) for key in ('pm','member','supplier','outside')])
    pid = project(state, pm, '业务验收'); state['browser_project'] = pid
    rid = round_create(pid, pm, '浏览器交付轮次', 'SUPPLIER'); state['browser_round'] = rid
    STATE.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding='utf-8')
    p = ok('POST','/projects',{'name':state['marker']+'项目创建','supplierId':state['supplier_ids'][0]},pm)
    updated = ok('PUT',f"/projects/{p['id']}",{'name':state['marker']+'项目创建已更新','supplierId':state['supplier_ids'][0]},pm)
    check('项目不再依赖历史编码且创建后可编辑',updated['name'].endswith('已更新') and 'code' not in updated)
    for token in [member,supplier]: check('授权角色可读取测试项目',request('GET',f'/projects/{pid}',token=token)[0]==200)
    check('其他供应商无权读取项目',request('GET',f'/projects/{pid}',token=outside)[0]==403)

    files=[upload(pid,rid,pm,data,'同名验收.pdf') for data in (b'first-file',b'second-file')]
    data=ok('POST','/files/batch-download',{'ids':[f['id'] for f in files]},pm)
    archive=zipfile.ZipFile(io.BytesIO(data)); bodies=sorted(archive.read(name) for name in archive.namelist())
    check('真实 ZIP 同名文件下载内容均保留',len(set(archive.namelist()))==2 and bodies==[b'first-file',b'second-file'])
    for f,expected in zip(files,(b'first-file',b'second-file')):
        data=ok('GET',f"/files/{f['id']}/download",token=supplier)
        check('真实鉴权下载及 SHA256 内容一致',data==expected and hashlib.sha256(data).hexdigest()==f['sha256'])
    check('越权文件下载被拒',request('GET',f"/files/{files[0]['id']}/download",token=outside)[0]==403)

    content=(b'RESUME-CONTENT-'*160000)
    session=init(pid,rid,member,content,'断点续传.zip');sid=session['sessionId']; size=session['chunkSize']
    ok('PUT',f'/uploads/{sid}/chunks/0',token=member,raw=content[:size])
    resumed=init(pid,rid,member,content,'断点续传.zip')
    check('真实请求按文件摘要恢复已传分片',resumed['sessionId']==sid and resumed['uploadedChunks']==[0])
    for i in range(1,session['totalChunks']):ok('PUT',f'/uploads/{sid}/chunks/{i}',token=member,raw=content[i*size:(i+1)*size])
    f=ok('POST',f'/uploads/{sid}/merge',{},member)
    check('续传后完整下载校验一致',ok('GET',f"/files/{f['id']}/download",token=pm)==content)
    a=init(pid,rid,member,b'old!','无摘要.pdf',False);b=init(pid,rid,member,b'new!','无摘要.pdf',False)
    check('旧客户端无摘要不会误复用会话',a['sessionId']!=b['sessionId'])
    session=init(pid,rid,member,b'good','坏摘要.pdf');sid=session['sessionId']
    ok('PUT',f'/uploads/{sid}/chunks/0',token=member,raw=b'evil')
    check('实际内容与摘要不符拒绝合并',request('POST',f'/uploads/{sid}/merge',{},member)[0]==400)
    ok('DELETE',f'/uploads/{sid}',token=member)

    session=init(pid,rid,member,b'test','撤员验收.pdf');sid=session['sessionId']
    ok('PUT',f'/uploads/{sid}/chunks/0',token=member,raw=b'test')
    ok('PUT',f'/projects/{pid}/members',{'userIds':[state['users']['pm']['id']]},pm)
    check('撤员后已有上传会话读写和合并均被拒',all(status==403 for status in [request('GET',f'/uploads/{sid}',token=member)[0],request('PUT',f'/uploads/{sid}/chunks/0',token=member,raw=b'test')[0],request('POST',f'/uploads/{sid}/merge',{},member)[0]]))
    ok('PUT',f'/projects/{pid}/members',{'userIds':[state['users']['pm']['id'],state['users']['member']['id']]},pm)
    user=state['users']['member'];long='A1'+'a'*127
    check('用户密码重置拒绝超过登录字节上限',request('PUT',f"/admin/users/{user['id']}/password",{'newPassword':long},admin)[0]==400)
    check('本人改密拒绝超过登录字节上限',request('PUT','/auth/password',{'oldPassword':user['password'],'newPassword':long},member)[0]==400)
    check('供应商密码重置拒绝超长密码',request('PUT',f"/admin/supplier-accounts/{state['users']['supplier']['id']}/password",{'newPassword':long},admin)[0]==400)
    message=ok('POST',f'/projects/{pid}/messages',{'roundId':rid,'content':state['marker']+' API 留言与已读验收'},pm)
    ok('POST','/messages/read',{'ids':[message['id']]},supplier)
    receipt=ok('GET',f"/messages/{message['id']}/reads",token=pm)
    check('留言与真实已读回执落库',any(r['userId']==state['users']['supplier']['id'] for r in receipt.get('readers',[])))

def final_commit_barriers(state):
    pm,member=login(state['users']['pm']),login(state['users']['member'])
    from live_context import config
    folder=Path(config()['storage']['root'])/'files'
    for action,expected in [('round',409),('project',409),('membership',403)]:
        pid=project(state,pm,'提交屏障'+action);rid=round_create(pid,pm)
        session=init(pid,rid,member);sid=session['sessionId']
        ok('PUT',f'/uploads/{sid}/chunks/0',token=member,raw=b'test')
        before={p for p in folder.rglob('*') if p.is_file()}
        connection=db();connection.begin()
        try:
            with connection.cursor() as cursor:cursor.execute('SELECT id FROM projects WHERE id=%s FOR UPDATE',(pid,))
            with concurrent.futures.ThreadPoolExecutor() as pool:
                future=pool.submit(request,'POST',f'/uploads/{sid}/merge',{},member)
                try:wait_for_db_wait(connection.thread_id(), future=future)
                except Exception:connection.rollback();raise
                with connection.cursor() as cursor:
                    if action=='round':cursor.execute("UPDATE rounds SET status='CONFIRMED' WHERE id=%s",(rid,))
                    elif action=='project':cursor.execute("UPDATE projects SET status='COMPLETED' WHERE id=%s",(pid,))
                    else:cursor.execute('DELETE FROM project_members WHERE project_id=%s AND user_id=%s',(pid,state['users']['member']['id']))
                connection.commit();status,_=future.result()
            with connection.cursor() as cursor:
                cursor.execute('SELECT COUNT(*) AS n FROM files WHERE round_id=%s',(rid,));files=cursor.fetchone()['n']
                cursor.execute('SELECT COUNT(*) AS n FROM email_outbox WHERE project_id=%s',(pid,));mails=cursor.fetchone()['n']
            after={p for p in folder.rglob('*') if p.is_file()}
            check('最终提交屏障 '+action+' 先提交后上传拒绝且无文件/通知残留',status==expected and files==0 and mails==0 and before==after,{'http':status,'files':files,'outbox':mails,'disk_added':len(after-before)})
        finally:connection.rollback();connection.close()

def smtp_acceptance(state):
    pm=login(state['users']['pm']);pid=state['browser_project']
    with db() as connection,connection.cursor() as cursor:
        cursor.execute('SELECT COALESCE(MAX(id),0) AS n FROM email_outbox');before=cursor.fetchone()['n']
        cursor.execute("INSERT INTO email_outbox(event_type,project_id,recipient_email,subject,body,status,retry_count,created_at) VALUES('MESSAGE_CREATED',%s,%s,%s,%s,'PENDING',0,UTC_TIMESTAMP())",(pid,'invalid-address',state['marker']+' invalid recipient probe','Synthetic invalid recipient acceptance'))
        invalid_id=cursor.lastrowid
    rid=round_create(pid,pm,'SMTP 驳回通知')
    ok('POST',f'/rounds/{rid}/reject',{'reason':state['marker']+' SMTP 驳回验收'},pm)
    rid=round_create(pid,pm,'SMTP 撤销通知')
    ok('POST',f'/rounds/{rid}/cancel',{},pm)
    ok('POST',f'/projects/{pid}/messages',{'content':state['marker']+' SMTP 无效地址之后的正常通知验收，请忽略。'},pm)
    deadline=time.monotonic()+150
    while True:
        with db() as connection,connection.cursor() as cursor:
            cursor.execute('SELECT id,event_type,status,retry_count,last_error,sent_at FROM email_outbox WHERE project_id=%s AND id>%s ORDER BY id',(pid,before));rows=cursor.fetchall()
        if len(rows)>=4 and all(row['status'] in ('SENT','FAILED') for row in rows):break
        if time.monotonic()>deadline:break
        time.sleep(2)
    invalid=[row for row in rows if row['id']==invalid_id]
    valid=[row for row in rows if row['id']!=invalid_id]
    check('非法地址终止发送且不回退其他收件人',len(invalid)==1 and invalid[0]['status']=='FAILED' and invalid[0]['retry_count']==1 and invalid[0]['sent_at'] is None and invalid[0]['last_error']=='发件/收件地址配置无效')
    check('真实 SMTP 驳回、撤销、留言通知全部发送成功且 worker 保持运行',len(valid)==3 and all(row['status']=='SENT' and row['sent_at'] and row['retry_count']==0 for row in valid),{'statuses':[row['status'] for row in valid]})
    with db() as connection,connection.cursor() as cursor:
        cursor.execute("SELECT e.event_type,COUNT(*) AS n FROM email_outbox e JOIN projects p ON p.id=e.project_id WHERE p.name LIKE %s AND e.status='SENT' GROUP BY e.event_type",(state['marker']+'%',));types={row['event_type']:row['n'] for row in cursor.fetchall()}
    check('五类业务通知均有真实 SMTP 成功记录',set(types)=={'FILE_UPLOADED','MESSAGE_CREATED','ROUND_CONFIRMED','ROUND_REJECTED','ROUND_CANCELLED'},types)
    state['smtp_invalid_id']=invalid_id
    STATE.write_text(json.dumps(state,ensure_ascii=False,indent=2),encoding='utf8')

if __name__ == '__main__':
    stage = sys.argv[1] if len(sys.argv)>1 else 'setup'
    state = setup() if stage == 'setup' else json.loads(STATE.read_text(encoding='utf-8'))
    if stage == 'concurrency': concurrency(state)
    if stage == 'lock_order': lock_order(state)
    if stage == 'business': business(state)
    if stage == 'commit_barriers': final_commit_barriers(state)
    if stage == 'smtp': smtp_acceptance(state)
    if any(not x['passed'] for x in results): sys.exit(1)
