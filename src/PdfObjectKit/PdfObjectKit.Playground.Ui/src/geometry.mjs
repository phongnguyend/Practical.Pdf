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

export function hitTest(objects, viewport, x, y, preferAnnotation = false) {
  const hits = [...objects].reverse().filter(object => containsPoint(
    object.corners.map(point => viewport.convertToViewportPoint(point.x, point.y)), x, y));
  const top = hits[0];
  if (preferAnnotation) {
    return hits.find(object => object.kind === 'annotation') ?? top;
  }
  // Text markup decorates the text; it must not steal its selection/drag target.
  // Comments remain clickable, and markup can still be selected in the object list.
  if (top?.kind === 'annotation' && ['Highlight', 'Underline', 'StrikeOut'].includes(top.details?.kind)) {
    return hits.find(object => object.kind === 'text') ?? top;
  }
  return top;
}
