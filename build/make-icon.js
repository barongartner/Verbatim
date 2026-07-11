'use strict';
// Renders build/icon.png (1024px) from an inline SVG. electron-builder
// derives .icns and .ico from it automatically. Run: npm run icon

const path = require('path');
const sharp = require('sharp');

const svg = `
<svg width="1024" height="1024" viewBox="0 0 1024 1024" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#131a24"/>
      <stop offset="1" stop-color="#0a0e14"/>
    </linearGradient>
    <linearGradient id="bar" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#7fd8ff"/>
      <stop offset="1" stop-color="#1e8fd0"/>
    </linearGradient>
  </defs>
  <rect x="64" y="64" width="896" height="896" rx="200" fill="url(#bg)"/>
  <rect x="64" y="64" width="896" height="896" rx="200" fill="none"
        stroke="#274357" stroke-width="10"/>
  <g fill="url(#bar)">
    <rect x="216" y="438" width="60" height="148" rx="30"/>
    <rect x="326" y="352" width="60" height="320" rx="30"/>
    <rect x="436" y="252" width="60" height="520" rx="30"/>
    <rect x="546" y="322" width="60" height="380" rx="30"/>
    <rect x="656" y="412" width="60" height="200" rx="30"/>
    <rect x="766" y="462" width="60" height="100" rx="30"/>
  </g>
  <circle cx="466" cy="812" r="26" fill="#7fd8ff"/>
</svg>`;

sharp(Buffer.from(svg))
  .resize(1024, 1024)
  .png()
  .toFile(path.join(__dirname, 'icon.png'))
  .then(() => console.log('build/icon.png written'))
  .catch((e) => { console.error(e); process.exit(1); });
