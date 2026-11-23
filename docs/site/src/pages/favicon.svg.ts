import mark from '../assets/mark.svg?raw';

export const GET = () => new Response(mark, { headers: { 'Content-Type': 'image/svg+xml' } });
