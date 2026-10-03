/** Preserve one meter's aspect ratio, including floors with nonzero/negative origins. */
export function squareBounds(bounds: number[][]): number[][] {
	const side = Math.max(bounds[1][0] - bounds[0][0], bounds[1][1] - bounds[0][1]);
	return [bounds[0], [bounds[0][0] + side, bounds[0][1] + side, bounds[1][2]]];
}
