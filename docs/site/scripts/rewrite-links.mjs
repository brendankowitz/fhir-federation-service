import { parse, postprocess, preprocess } from 'micromark';
import { decodeString } from 'micromark-util-decode-string';
import { gfm } from 'micromark-extension-gfm';

// Splice only parsed destinations: titles, labels, formatting and literal code
// remain byte-for-byte as authored. Work backwards to keep source offsets valid.
export function rewriteLinks(markdown, resolve) {
  const parser = parse({ extensions: [gfm()] });
  const events = postprocess(parser.document().write(preprocess()(markdown, undefined, true)));
  for (const [event, token] of events.reverse()) {
    if (event !== 'enter' || !['resourceDestinationString', 'definitionDestinationString'].includes(token.type)) continue;
    const { start, end } = token;
    const url = decodeString(markdown.slice(start.offset, end.offset));
    const target = resolve(url);
    if (target !== url) markdown = markdown.slice(0, start.offset) + target + markdown.slice(end.offset);
  }
  return markdown;
}
