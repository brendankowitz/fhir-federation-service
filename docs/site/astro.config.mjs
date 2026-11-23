import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import { execFileSync } from 'node:child_process';

export default defineConfig({
  site: 'https://brendankowitz.github.io',
  base: '/fhir-federation-service/',
  trailingSlash: 'always',
  vite: { define: {
    'import.meta.env.PUBLIC_DOCS_REVISION': JSON.stringify(execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim()),
  } },
  integrations: [
    starlight({
      title: 'Ignixa Federation',
      logo: { src: './src/assets/mark.svg' },
      customCss: ['./src/styles/federation.css'],
      components: { PageTitle: './src/components/PageTitle.astro' },
      sidebar: [
        { label: 'Start here', slug: '' },
        { label: 'Operate a gateway', items: [
          'operator', 'architecture', 'quickstart', 'deployment', 'binding',
          'shards', 'authentication', 'status', 'server-paging', 'limits',
          'errors', 'observability', 'security', 'troubleshooting',
        ] },
        { label: 'Build a client', items: [
          'client-developer-guide', 'client-http', 'client-bundles', 'client-reads',
          'client-paging', 'client-errors', 'sample-client',
        ] },
        { label: 'Reference', items: ['configuration', 'contracts', 'client-api'] },
      ],
    }),
  ],
});
