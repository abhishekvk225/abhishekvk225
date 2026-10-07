// Camera access for the FaceCapture component. ES module imported on demand.
// Nothing is stored or uploaded here: frames are returned to .NET as a base64 JPEG and the stream is stopped on dispose.

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

// Returns a base64 JPEG (no data: prefix) or '' when no frame is available.
export function capture(video, quality) {
  try {
    if (!video || !video.videoWidth) { return ''; }
    const canvas = document.createElement('canvas');
    canvas.width = video.videoWidth;
    canvas.height = video.videoHeight;
    canvas.getContext('2d').drawImage(video, 0, 0);
    const url = canvas.toDataURL('image/jpeg', quality || 0.92);
    return url.substring(url.indexOf(',') + 1);
  } catch (e) {
    return '';
  }
}

export function stop(video) {
  try {
    const stream = video && video.srcObject;
    if (stream && stream.getTracks) { stream.getTracks().forEach(function (t) { t.stop(); }); }
    if (video) { video.srcObject = null; }
  } catch (e) { /* nothing to release */ }
}
