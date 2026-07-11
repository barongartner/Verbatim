'use strict';

// Minimal 16-bit PCM mono WAV writer used to hand audio to the sherpa-onnx CLIs.

function wavHeader(numSamples, sampleRate) {
  const dataBytes = numSamples * 2;
  const h = Buffer.alloc(44);
  h.write('RIFF', 0);
  h.writeUInt32LE(36 + dataBytes, 4);
  h.write('WAVE', 8);
  h.write('fmt ', 12);
  h.writeUInt32LE(16, 16);          // fmt chunk size
  h.writeUInt16LE(1, 20);           // PCM
  h.writeUInt16LE(1, 22);           // mono
  h.writeUInt32LE(sampleRate, 24);
  h.writeUInt32LE(sampleRate * 2, 28); // byte rate
  h.writeUInt16LE(2, 32);           // block align
  h.writeUInt16LE(16, 34);          // bits per sample
  h.write('data', 36);
  h.writeUInt32LE(dataBytes, 40);
  return h;
}

// pcm: Int16Array. Writes [startSample, endSample) to filePath.
async function writeWavSlice(fs, filePath, pcm, sampleRate, startSample, endSample) {
  const s = Math.max(0, Math.min(startSample, pcm.length));
  const e = Math.max(s, Math.min(endSample, pcm.length));
  const slice = pcm.subarray(s, e);
  const body = Buffer.from(slice.buffer, slice.byteOffset, slice.length * 2);
  await fs.promises.writeFile(filePath, Buffer.concat([wavHeader(slice.length, sampleRate), body]));
}

module.exports = { wavHeader, writeWavSlice };
