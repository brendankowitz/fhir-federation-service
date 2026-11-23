import type { APIRoute } from 'astro';
import routes from '../../../../tools/legacy-docs-urls.json';

export function getStaticPaths() {
  return Object.keys(routes).filter(name => name !== 'index.html').map(name => ({
    params: { legacy: name.slice(0, -'.html'.length) },
  }));
}

export const GET: APIRoute = ({ params }) => {
  const target = `${import.meta.env.BASE_URL}${params.legacy}/`;
  return new Response(`<!doctype html><html lang="en"><head><meta charset="utf-8">
<title>Page moved</title><meta name="robots" content="noindex">
<link rel="canonical" href="${target}">
<script>location.replace(${JSON.stringify(target)} + location.search + location.hash)</script>
<meta http-equiv="refresh" content="0; url=${target}"></head>
<body><a href="${target}">Continue to the guide</a></body></html>`);
};
