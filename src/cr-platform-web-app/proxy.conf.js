const target = process.env.PLATFORM_API_TARGET ?? process.env.API_HTTPS ?? process.env.API_HTTP;

if (!target) {
  throw new Error('Set PLATFORM_API_TARGET when running the Angular app without Aspire.');
}

let url;
try {
  url = new URL(target);
} catch {
  throw new Error('PLATFORM_API_TARGET must be an absolute HTTP or HTTPS URL.');
}

if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.pathname !== '/'
  || url.search || url.hash) {
  throw new Error('The platform API target must be an HTTP or HTTPS origin without credentials or a path.');
}

module.exports = {
  '/api/games/live': {
    target: url.origin,
    secure: false,
    changeOrigin: true,
    ws: true,
  },
  '/api/**': {
    target: url.origin,
    secure: false,
    changeOrigin: true,
  },
};
