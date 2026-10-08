// Camera access for the FaceCapture component. ES module imported on demand.
// Nothing is stored or uploaded here: frames are handed to .NET as a stream and the camera is stopped on dispose.

export function isSupported() {
  try {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
  } catch (e) {
    return false;
  }
}

// Returns '' on success or a short error code the component maps to plain-language text.
export async function start(video) {
  try {
    if (!isSupported()) { return 'unsupported'; }
    stop(video);
    const stream = await navigator.mediaDevices.getUserMedia({
      video: { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 960 } },
      audio: false
    });
    video.srcObject = stream;
    await video.play();
    return '';
  } catch (e) {
    const name = e && e.name ? e.name : 'error';
    if (name === 'NotAllowedError' || name === 'SecurityError') { return 'denied'; }
    if (name === 'NotFoundError' || name === 'OverconstrainedError') { return 'nodevice'; }
    if (name === 'NotReadableError') { return 'busy'; }
    return 'error';
  }
}

// Returns the current frame as a JPEG Blob (or null). .NET reads it as a stream in small chunks, so the SignalR hub keeps its default
// message-size limit; a base64 string of the whole frame would be rejected (or force a large global limit that is a memory-DoS lever).
export function captureStream(video, quality) {
  return new Promise(function (resolve) {
    try {
      if (!video || !video.videoWidth) { resolve(null); return; }
      const canvas = document.createElement('canvas');
      canvas.width = video.videoWidth;
      canvas.height = video.videoHeight;
      canvas.getContext('2d').drawImage(video, 0, 0);
      canvas.toBlob(function (blob) { resolve(blob); }, 'image/jpeg', quality || 0.92);
    } catch (e) {
      resolve(null);
    }
  });
}

export function stop(video) {
  try {
    const stream = video && video.srcObject;
    if (stream && stream.getTracks) { stream.getTracks().forEach(function (t) { t.stop(); }); }
    if (video) { video.srcObject = null; }
  } catch (e) { /* nothing to release */ }
}
