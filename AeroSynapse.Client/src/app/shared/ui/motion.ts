/** True when the viewer asked for reduced motion, or the environment cannot animate. */
export function prefersReducedMotion(): boolean {
  return (
    typeof matchMedia !== 'function' ||
    typeof requestAnimationFrame !== 'function' ||
    matchMedia('(prefers-reduced-motion: reduce)').matches
  );
}
