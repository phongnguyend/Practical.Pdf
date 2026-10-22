// A polygon hit test also handles rotated or skewed image placements.
export function containsPoint(points, x, y) {
  let inside = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [xi, yi] = points[i];
    const [xj, yj] = points[j];
    if (((yi > y) !== (yj > y)) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) {
      inside = !inside;
    }
  }
  return inside;
}

export function hitTest(objects, viewport, x, y) {
  return [...objects].reverse().find(object => containsPoint(
    object.corners.map(point => viewport.convertToViewportPoint(point.x, point.y)), x, y));
}
