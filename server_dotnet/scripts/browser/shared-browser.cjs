module.exports = async function openBrowser(chromium) {
  const endpoint = process.env.YF_BROWSER_WS_ENDPOINT;
  if (!endpoint) throw new Error('Run browser steps through test-browser.py; it owns one shared isolated Chrome process.');
  return chromium.connect(endpoint);
};
