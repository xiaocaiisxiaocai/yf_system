const { chromium } = require('playwright');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const JSZip = require(process.env.YF_PROJECT_ROOT + '/web/node_modules/jszip');
const { fs, assert, OUT, s, f, record, login, api, projectMetadata, action, track } = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

async function uploadApi(context, token, projectId, fileName, bytes) {
  const fileMd5 = crypto.createHash('md5').update(bytes).digest('hex');
  const initialized = await (await api(context, 'POST', '/uploads/init', {
    projectId, fileName, fileSize: bytes.length, fileMd5,
  }, token)).json();
  for (let index = 0; index < initialized.totalChunks; index++) {
    const chunk = bytes.subarray(index * initialized.chunkSize,
      Math.min((index + 1) * initialized.chunkSize, bytes.length));
    const response = await context.request.fetch(
      s.base + '/api/v1/uploads/' + initialized.sessionId + '/chunks/' + index, {
        method: 'PUT', data: chunk,
        headers: {
          Origin: s.base,
          Authorization: 'Bearer ' + token,
          'Content-Type': 'application/octet-stream',
        },
      });
    assert.equal(response.status(), 200, 'upload chunk ' + index + ' for ' + fileName);
  }
  return (await api(context, 'POST', '/uploads/' + initialized.sessionId + '/merge', undefined, token)).json();
}

async function createProject(context, token, supplierId, name) {
  const group = await (await api(context, 'POST', '/project-groups', {
    name,
    description: '图片、PPTX、视频和留言图片浏览器预览夹具',
    supplierId,
    workOrderNos: ['PREVIEW-' + crypto.randomBytes(4).toString('hex')],
    machineModel: '预览验收机型',
    ...await projectMetadata(context, token, supplierId),
    subprojectNames: [name + ' 子项目'],
  }, token)).json();
  const detail = await (await api(context, 'GET', '/project-groups/' + group.id, undefined, token)).json();
  assert.equal(detail.projects.length, 1);
  return detail.projects[0];
}

async function createBrowserMedia(browser, marker) {
  const context = await browser.newContext({ viewport: { width: 640, height: 360 } });
  const page = await context.newPage();
  const pngPath = OUT + '/preview-fixture-' + marker + '.png';
  try {
    await page.setContent('<!doctype html><style>html,body{margin:0;width:100%;height:100%;overflow:hidden}'
      + 'main{width:100%;height:100%;display:grid;place-items:center;background:linear-gradient(135deg,#155eef,#7c3aed);color:white;font:700 34px Segoe UI}'
      + '</style><main>PREVIEW ' + marker + '</main>');
    await page.screenshot({ path: pngPath });
    const video = await page.evaluate(async label => {
      if (typeof MediaRecorder === 'undefined' || !HTMLCanvasElement.prototype.captureStream) {
        throw new Error('Chrome MediaRecorder/canvas.captureStream is required');
      }
      const mimeType = ['video/webm;codecs=vp9', 'video/webm;codecs=vp8', 'video/webm']
        .find(type => MediaRecorder.isTypeSupported(type));
      if (!mimeType) throw new Error('Chrome WebM recording support is required');
      const canvas = document.createElement('canvas');
      canvas.width = 320; canvas.height = 180;
      const drawing = canvas.getContext('2d');
      drawing.fillStyle = '#155eef'; drawing.fillRect(0, 0, canvas.width, canvas.height);
      const stream = canvas.captureStream(0);
      const videoTrack = stream.getVideoTracks()[0];
      assertVideoTrack(videoTrack);
      const recorder = new MediaRecorder(stream, { mimeType, videoBitsPerSecond: 500000 });
      const chunks = [];
      recorder.ondataavailable = event => { if (event.data.size) chunks.push(event.data); };
      const stopped = new Promise((resolve, reject) => {
        recorder.onstop = resolve; recorder.onerror = () => reject(recorder.error || new Error('recording failed'));
      });
      recorder.start();
      for (let frame = 0; frame < 32; frame++) {
        drawing.fillStyle = `hsl(${210 + frame * 4} 78% 45%)`;
        drawing.fillRect(0, 0, canvas.width, canvas.height);
        drawing.fillStyle = '#fff'; drawing.font = '700 24px sans-serif';
        drawing.fillText('VIDEO ' + label, 34, 70);
        drawing.fillStyle = '#fbbf24'; drawing.fillRect(20 + frame * 7, 105, 32, 32);
        videoTrack.requestFrame();
        await new Promise(resolve => setTimeout(resolve, 50));
      }
      recorder.requestData();
      await new Promise(resolve => setTimeout(resolve, 150));
      recorder.stop(); await stopped;
      stream.getTracks().forEach(track => track.stop());
      const blob = new Blob(chunks, { type: mimeType });
      const bytes = Array.from(new Uint8Array(await blob.arrayBuffer()));
      return { bytes, mimeType: blob.type };

      function assertVideoTrack(track) {
        if (!track || typeof track.requestFrame !== 'function') {
          throw new Error('Chrome CanvasCaptureMediaStreamTrack.requestFrame is required');
        }
      }
    }, marker);
    let webm = Buffer.from(video.bytes);
    let videoMime = video.mimeType;
    if (webm.length < 1000) {
      const webmPath = OUT + '/preview-fixture-' + marker + '.webm';
      const python = [
        'import cv2, numpy as np, sys',
        'out = cv2.VideoWriter(sys.argv[1], cv2.VideoWriter_fourcc(*"VP80"), 20.0, (320, 180))',
        'assert out.isOpened(), "OpenCV VP8 WebM encoder unavailable"',
        'for frame_no in range(32):',
        '    frame = np.zeros((180, 320, 3), dtype=np.uint8)',
        '    frame[:, :, 0] = (frame_no * 9) % 255',
        '    frame[:, :, 1] = (90 + frame_no * 5) % 255',
        '    frame[:, :, 2] = (180 + frame_no * 3) % 255',
        '    cv2.putText(frame, "YF VIDEO", (78, 78), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (255,255,255), 2, cv2.LINE_AA)',
        '    cv2.putText(frame, f"{frame_no / 20:.2f} s", (118, 116), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (255,255,255), 1, cv2.LINE_AA)',
        '    out.write(frame)',
        'out.release()',
      ].join('\n');
      const generated = spawnSync('python', ['-c', python, webmPath], { encoding: 'utf8' });
      if (generated.status !== 0) {
        throw new Error('OpenCV WebM generation failed: ' + (generated.stderr || generated.stdout));
      }
      webm = fs.readFileSync(webmPath);
      videoMime = 'video/webm;codecs=vp8';
    }
    assert(webm.length > 1000, 'self-generated WebM fixture must be nonempty');
    return { png: fs.readFileSync(pngPath), webm, videoMime };
  } finally {
    await context.close();
  }
}

async function createPptx(marker) {
  // Trimmed parts of vue-office's test.pptx (slide 1, master, 11 layouts, theme, table styles),
  // committed so a fresh clone does not depend on the git-ignored third_party archive.
  const templatePath = process.env.YF_PROJECT_ROOT
    + '/server_dotnet/scripts/browser/fixtures/pptx-template.zip';
  assert(fs.existsSync(templatePath), 'repository PPTX template is required');
  const template = await JSZip.loadAsync(fs.readFileSync(templatePath), { checkCRC32: true });
  const output = new JSZip();
  const copy = async name => {
    const entry = template.file(name);
    assert(entry, 'PPTX template part: ' + name);
    output.file(name, await entry.async('nodebuffer'));
  };
  const layoutOverrides = Array.from({ length: 11 }, (_, index) =>
    `<Override PartName="/ppt/slideLayouts/slideLayout${index + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>`).join('');
  output.file('[Content_Types].xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/><Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/><Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>${layoutOverrides}<Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/><Override PartName="/ppt/tableStyles.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml"/></Types>`);
  output.file('_rels/.rels', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/></Relationships>`);
  let presentation = await template.file('ppt/presentation.xml').async('string');
  presentation = presentation
    .replace(/<p:sldMasterIdLst>[\s\S]*?<\/p:sldMasterIdLst>/,
      '<p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst>')
    .replace(/<p:notesMasterIdLst>[\s\S]*?<\/p:notesMasterIdLst>/, '')
    .replace(/<p:sldIdLst>[\s\S]*?<\/p:sldIdLst>/,
      '<p:sldIdLst><p:sldId id="256" r:id="rId3"/></p:sldIdLst>');
  output.file('ppt/presentation.xml', presentation);
  output.file('ppt/_rels/presentation.xml.rels', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="slideMasters/slideMaster1.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/><Relationship Id="rId19" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="theme/theme1.xml"/><Relationship Id="rId20" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/tableStyles" Target="tableStyles.xml"/></Relationships>`);
  let slide = await template.file('ppt/slides/slide1.xml').async('string');
  slide = slide.replace('<a:t>pptxPreview</a:t>', `<a:t>PPTX ${marker}</a:t>`)
    .replace('<a:t>能力测试</a:t>', '<a:t></a:t>');
  assert(slide.includes(`PPTX ${marker}`), 'PPTX marker replacement');
  output.file('ppt/slides/slide1.xml', slide);
  await copy('ppt/slides/_rels/slide1.xml.rels');
  await copy('ppt/slideMasters/slideMaster1.xml');
  await copy('ppt/slideMasters/_rels/slideMaster1.xml.rels');
  for (let index = 1; index <= 11; index++) {
    await copy(`ppt/slideLayouts/slideLayout${index}.xml`);
    await copy(`ppt/slideLayouts/_rels/slideLayout${index}.xml.rels`);
  }
  await copy('ppt/theme/theme1.xml');
  await copy('ppt/tableStyles.xml');
  return output.generateAsync({ type: 'nodebuffer', compression: 'DEFLATE' });
}

(async () => {
  let browser, page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const admin = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const uploadContext = await browser.newContext();
    page = await admin.newPage(); track(page, 'preview-extras');
    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const adminToken = auth.accessToken;
    const suffix = crypto.randomBytes(5).toString('hex');
    const media = await createBrowserMedia(browser, suffix);
    assert(media.png.length > 100 && media.webm.length > 100, 'browser-generated visual fixtures must be nonempty');
    const pptx = await createPptx(suffix);
    const archive = await JSZip.loadAsync(pptx);
    assert(archive.file('ppt/slides/slide1.xml'), 'generated PPTX must contain slide1.xml');

    const project = await createProject(admin, adminToken, f.suppliers.a.id,
      '附加预览验收-' + suffix);
    await api(admin, 'PUT', '/projects/' + project.id + '/status', { status: 'IN_PROGRESS' }, adminToken);
    const imageName = 'browser-image-' + suffix + '.png';
    const pptxName = 'browser-slides-' + suffix + '.pptx';
    const videoName = 'browser-video-' + suffix + '.webm';
    await uploadApi(uploadContext, adminToken, project.id, imageName, media.png);
    await uploadApi(uploadContext, adminToken, project.id, pptxName, pptx);
    await uploadApi(uploadContext, adminToken, project.id, videoName, media.webm);

    const initial = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/projects/' + project.id + '/files'
      && response.request().method() === 'GET' && response.status() === 200);
    await page.goto(s.base + '/projects/' + project.id); await initial;
    await page.getByRole('tab', { name: '文件', exact: true, selected: true }).waitFor();
    const row = name => page.getByRole('row').filter({ has: page.getByText(name, { exact: true }) });
    const fileDialog = () => page.locator('.file-preview-modal:visible');
    const assertWatermark = async (dialog, expectedEmployeeNo = 'admin', expectedRealName = '系统管理员') => {
      const layer = dialog.locator('.preview-watermark');
      await layer.waitFor({ state: 'visible' });
      assert.equal(await layer.getAttribute('aria-hidden'), 'true');
      const label = await layer.locator('span').first().textContent();
      const escapeRegExp = value => value.replace(/[.*+?^${}()|[\\]\\\\]/g, '\\\\$&');
      assert.match(label || '', new RegExp('^' + escapeRegExp(expectedEmployeeNo) + ' ' + escapeRegExp(expectedRealName) + ' \\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}$'));
    };

    await record('Playwright截图文件真实渲染且仅图片滚轮缩放，预览无下载入口', async () => {
      await row(imageName).getByRole('button', { name: '预览文件', exact: true }).click();
      const dialog = fileDialog();
      const image = dialog.getByRole('img', { name: imageName, exact: true });
      await image.waitFor();
      await assertWatermark(dialog);
      const dimensions = await image.evaluate(element => ({ width: element.naturalWidth, height: element.naturalHeight }));
      assert.deepEqual(dimensions, { width: 640, height: 360 });
      assert.equal(await dialog.getByRole('button', { name: /下载/ }).count(), 0);
      const scale = dialog.locator('.image-preview-toolbar span[aria-live="polite"]');
      const before = await scale.textContent();
      await dialog.getByRole('region', { name: '图片预览内容', exact: true }).hover();
      await page.mouse.wheel(0, -120);
      await page.waitForFunction(previous => document.querySelector('.file-preview-modal .image-preview-toolbar span[aria-live="polite"]')?.textContent !== previous, before);
      assert.notEqual(await scale.textContent(), before);
      await dialog.getByRole('button', { name: '关闭文件预览', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
    });

    await record('最小PPTX由真实渲染器呈现内容且滚轮不改变缩放', async () => {
      await row(pptxName).getByRole('button', { name: '预览文件', exact: true }).click();
      const dialog = fileDialog();
      await dialog.getByText('/ 1 张', { exact: true }).waitFor();
      const frame = dialog.frameLocator('iframe[title="PPTX 预览内容"]');
      await frame.locator('.pptx-preview-slide-wrapper').waitFor();
      await frame.getByText('PPTX ' + suffix, { exact: true }).waitFor();
      await assertWatermark(dialog);
      assert.equal(await dialog.getByRole('button', { name: /下载/ }).count(), 0);
      const zoom = dialog.getByLabel('PPTX 缩放', { exact: true });
      const before = await zoom.textContent();
      await frame.locator('body').hover(); await page.mouse.wheel(0, -120); await page.waitForTimeout(100);
      assert.equal(await zoom.textContent(), before, 'PPTX wheel must remain normal viewport scrolling');
      await dialog.getByRole('button', { name: '关闭文件预览', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
    });

    await record('浏览器自产WebM取得媒体授权并真实加载播放', async () => {
      await row(videoName).getByRole('button', { name: '预览文件', exact: true }).click();
      const dialog = fileDialog();
      const video = dialog.getByLabel('视频预览', { exact: true });
      await video.waitFor();
      await page.waitForFunction(() => {
        const element = document.querySelector('.file-preview-modal video');
        return element instanceof HTMLVideoElement && element.readyState >= HTMLMediaElement.HAVE_METADATA
          && element.videoWidth > 0 && element.videoHeight > 0;
      });
      const playback = await video.evaluate(async element => {
        element.muted = true; await element.play();
        await new Promise(resolve => setTimeout(resolve, 180));
        const result = { currentTime: element.currentTime, width: element.videoWidth, height: element.videoHeight,
          readyState: element.readyState, controlsList: element.getAttribute('controlsList') || '' };
        element.pause(); return result;
      });
      assert(playback.currentTime > 0 && playback.width === 320 && playback.height === 180, JSON.stringify(playback));
      assert(playback.controlsList.includes('nodownload'));
      assert.equal(await dialog.getByRole('button', { name: /下载/ }).count(), 0);
      await dialog.getByRole('button', { name: '关闭文件预览', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
    });

    await page.getByRole('tab', { name: /留言/ }).click();
    const input = page.getByPlaceholder('输入留言，Ctrl+Enter 发送', { exact: true });
    await input.waitFor();
    const uploadName = 'message-upload-' + suffix + '.png';
    const pasteName = 'message-paste-' + suffix + '.png';
    await record('留言图片选择与粘贴同时提交并在重载后持久化预览', async () => {
      await page.locator('.message-image-input').setInputFiles({ name: uploadName, mimeType: 'image/png', buffer: media.png });
      await page.getByLabel('已添加 1 张图片', { exact: true }).waitFor();
      await page.locator('.message-composer').evaluate((element, payload) => {
        const bytes = Uint8Array.from(payload.bytes);
        const transfer = new DataTransfer();
        transfer.items.add(new File([bytes], payload.name, { type: 'image/png' }));
        element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true, cancelable: true }));
      }, { bytes: Array.from(media.png), name: pasteName });
      await page.getByLabel('已添加 2 张图片', { exact: true }).waitFor();
      const content = '图片上传与粘贴持久化-' + suffix;
      await input.fill(content);
      const message = await action(page, '/projects/' + project.id + '/messages', 'POST', () =>
        page.getByRole('button', { name: '发送', exact: true }).click());
      assert.deepEqual(message.images.map(image => image.name), [uploadName, pasteName]);
      await page.getByText(content, { exact: true }).waitFor();
      const persisted = await (await api(admin, 'GET', '/projects/' + project.id + '/messages', undefined, adminToken)).json();
      const stored = persisted.list.find(item => item.id === message.id);
      assert(stored); assert.deepEqual(stored.images.map(image => image.name), [uploadName, pasteName]);
      for (const image of stored.images) {
        const response = await api(admin, 'GET', '/messages/' + message.id + '/images/' + image.id, undefined, adminToken);
        assert.equal(response.headers()['content-type'], 'image/png');
        assert.equal((await response.body()).length, media.png.length);
      }
      await page.reload(); await input.waitFor(); await page.getByText(content, { exact: true }).waitFor();
      const previewButton = page.getByRole('button', { name: '预览图片：' + pasteName, exact: true });
      await previewButton.click();
      const dialog = page.locator('.message-image-preview-modal:visible');
      const image = dialog.getByRole('img', { name: pasteName, exact: true }); await image.waitFor();
      await assertWatermark(dialog);
      assert.equal(await image.evaluate(element => element.naturalWidth), 640);
      assert.equal(await dialog.getByRole('button', { name: /下载/ }).count(), 0);
      const scale = dialog.locator('.image-preview-toolbar span[aria-live="polite"]');
      const before = await scale.textContent();
      await dialog.getByRole('region', { name: '图片预览内容', exact: true }).hover(); await page.mouse.wheel(0, -120);
      await page.waitForFunction(previous => document.querySelector('.message-image-preview-modal .image-preview-toolbar span[aria-live="polite"]')?.textContent !== previous, before);
      await dialog.getByRole('button', { name: '关闭图片预览', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
    });

    await page.screenshot({ path: OUT + '/preview-extras.png', fullPage: true });
    await admin.close(); await uploadContext.close();
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/preview-extras-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-5000));
    }
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
