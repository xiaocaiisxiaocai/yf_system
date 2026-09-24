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

async function initUpload(context, token, projectId, fileName, bytes, fileLastModified = FIXTURE_LAST_MODIFIED) {
  return (await api(context, 'POST', '/uploads/init', {
    projectId,
    fileName,
    fileSize: bytes.length,
    fileLastModified,
    fileFingerprint: fileFingerprint(fileName, bytes, fileLastModified),
  }, token)).json();
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

async function uploadFixture(context, token, projectId, fileName, bytes) {
  const initialized = await initUpload(context, token, projectId, fileName, bytes);
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
