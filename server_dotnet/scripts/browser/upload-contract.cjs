const crypto = require('node:crypto');
const {
  assert, s, api,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const FINGERPRINT_SAMPLE_SIZE = 1024 * 1024;
const FIXTURE_LAST_MODIFIED = 1700000000000;

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function fileFingerprint(fileName, bytes, fileLastModified = FIXTURE_LAST_MODIFIED) {
  const first = bytes.subarray(0, Math.min(FINGERPRINT_SAMPLE_SIZE, bytes.length));
  const last = bytes.subarray(Math.max(0, bytes.length - FINGERPRINT_SAMPLE_SIZE));
  const canonical = JSON.stringify([
    'yf-upload-fingerprint-v2',
    fileName,
    bytes.length,
    fileLastModified,
    sha256(first),
    sha256(last),
  ]);
  return sha256(Buffer.from(canonical, 'utf8'));
}

// 公司内部发给供应商的非 STEP 文件要求子项目已有 STEP 3D 图（上传资料要求契约-2026-09-24）。
// 夹具默认不做任何补救：规则拒绝会原样抛出。只有显式传 { ensureStep: true } 的调用方（内部账号向可能
// 还没有 STEP 的子项目上传非 STEP 夹具）才会在遇到该拒绝时先补传一个 STEP 文件再重试；
// 规则本身由 HTTP 契约与浏览器业务流程显式验证。
const STEP_REQUIRED_MARKER = '至少需要一个 STEP 格式 3D 图';
const COMPANY_STEP_FIXTURE_NAME = 'fixture-assembly.step';
const COMPANY_STEP_FIXTURE_BYTES = Buffer.from('ISO-10303-21; fixture assembly');

async function initUpload(
  context, token, projectId, fileName, bytes, fileLastModified = FIXTURE_LAST_MODIFIED, { ensureStep = false } = {},
) {
  const request = () => api(context, 'POST', '/uploads/init', {
    projectId,
    fileName,
    fileSize: bytes.length,
    fileLastModified,
    fileFingerprint: fileFingerprint(fileName, bytes, fileLastModified),
  }, token);
  try {
    return (await request()).json();
  } catch (error) {
    if (!ensureStep || !String(error && error.message).includes(STEP_REQUIRED_MARKER)) throw error;
  }
  await uploadFixture(context, token, projectId, COMPANY_STEP_FIXTURE_NAME, COMPANY_STEP_FIXTURE_BYTES);
  return (await request()).json();
}

async function putChunk(context, token, sessionId, index, chunk, label = '') {
  const response = await context.request.fetch(
    s.base + '/api/v1/uploads/' + sessionId + '/chunks/' + index,
    {
      method: 'PUT',
      data: chunk,
      headers: {
        Origin: s.base,
        Authorization: 'Bearer ' + token,
        'Content-Type': 'application/octet-stream',
        'X-Chunk-SHA256': sha256(chunk),
      },
    },
  );
  assert.equal(response.status(), 200, 'upload chunk ' + index + (label ? ' for ' + label : ''));
  return response;
}

async function uploadFixture(context, token, projectId, fileName, bytes, { ensureStep = false } = {}) {
  const initialized = await initUpload(
    context, token, projectId, fileName, bytes, FIXTURE_LAST_MODIFIED, { ensureStep },
  );
  for (let index = 0; index < initialized.totalChunks; index += 1) {
    const start = index * initialized.chunkSize;
    await putChunk(
      context,
      token,
      initialized.sessionId,
      index,
      bytes.subarray(start, Math.min(start + initialized.chunkSize, bytes.length)),
      fileName,
    );
  }
  await api(context, 'POST', '/uploads/' + initialized.sessionId + '/md5', {
    fileMd5: crypto.createHash('md5').update(bytes).digest('hex'),
  }, token);
  const merged = await (await api(
    context, 'POST', '/uploads/' + initialized.sessionId + '/merge', undefined, token,
  )).json();
  return { ...merged, sessionId: initialized.sessionId };
}

module.exports = {
  FIXTURE_LAST_MODIFIED,
  fileFingerprint,
  initUpload,
  putChunk,
  sha256,
  uploadFixture,
};
