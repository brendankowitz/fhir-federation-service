import assert from 'node:assert/strict';
import test from 'node:test';
import { rewriteLinks } from './rewrite-links.mjs';

test('rewrite real links, nested images and definitions but leave code examples untouched', () => {
  const fixture = [
    '[guide](topic.md "Guide") [Reference][ref]',
    '[![figure](topic.md)](topic.md)',
    '',
    '[ref]: <topic.md> "Example"',
    '',
    'Inline `[guide](topic.md)` and `` `[guide](topic.md)` ``.',
    '',
    '~~~md', '[guide](topic.md)', '[ref]: topic.md', '~~~',
    '',
    '````md', '```', '[guide](topic.md)', '```', '````',
    '',
    '    [guide](topic.md)',
  ].join('\n');
  const [realLinks, examples] = fixture.split('Inline ');
  assert.equal(rewriteLinks(fixture, () => '/published/'), realLinks.replaceAll('topic.md', '/published/') + 'Inline ' + examples);
});
