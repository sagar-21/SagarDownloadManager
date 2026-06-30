// Run with: node generate-icons.js
// Generates simple PNG icons for the extension.
// Requires Node.js — run once, then commit the icons/ folder.

const { createCanvas } = require('canvas');   // npm install canvas
const fs = require('fs');

function makeIcon(size) {
    const c   = createCanvas(size, size);
    const ctx = c.getContext('2d');
    const r   = size * 0.14;

    // Background
    const grad = ctx.createLinearGradient(0, 0, size, size);
    grad.addColorStop(0, '#2563eb');
    grad.addColorStop(1, '#1d4ed8');
    ctx.beginPath();
    ctx.moveTo(r, 0); ctx.lineTo(size - r, 0);
    ctx.arcTo(size, 0, size, r, r);
    ctx.lineTo(size, size - r);
    ctx.arcTo(size, size, size - r, size, r);
    ctx.lineTo(r, size);
    ctx.arcTo(0, size, 0, size - r, r);
    ctx.lineTo(0, r);
    ctx.arcTo(0, 0, r, 0, r);
    ctx.closePath();
    ctx.fillStyle = grad;
    ctx.fill();

    // Download arrow
    const s  = size * 0.55;
    const cx = size / 2;
    const cy = size / 2 + size * 0.04;
    ctx.strokeStyle = 'white';
    ctx.lineWidth   = size * 0.11;
    ctx.lineCap     = 'round';
    ctx.lineJoin    = 'round';

    // Vertical shaft
    ctx.beginPath();
    ctx.moveTo(cx, cy - s * 0.48);
    ctx.lineTo(cx, cy + s * 0.12);
    ctx.stroke();

    // Arrow head
    ctx.beginPath();
    ctx.moveTo(cx - s * 0.30, cy - s * 0.10);
    ctx.lineTo(cx, cy + s * 0.22);
    ctx.lineTo(cx + s * 0.30, cy - s * 0.10);
    ctx.stroke();

    // Underline bar
    ctx.beginPath();
    ctx.moveTo(cx - s * 0.38, cy + s * 0.42);
    ctx.lineTo(cx + s * 0.38, cy + s * 0.42);
    ctx.stroke();

    return c.toBuffer('image/png');
}

[16, 48, 128].forEach(sz => {
    fs.writeFileSync(`icons/icon${sz}.png`, makeIcon(sz));
    console.log(`icons/icon${sz}.png written`);
});
