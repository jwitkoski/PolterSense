import type { ScaleOrdinal, ScaleLinear } from 'd3';
import type { Writable } from 'svelte/store';
export interface LayerCakeContext {
	xScale: Writable<ScaleLinear<number, number, never>>;
	yScale: Writable<ScaleLinear<number, number, never>>;
	width: Writable<number>;
	height: Writable<number>;
	padding: Writable<{ top: number; right: number; bottom: number; left: number }>;
	colors: ScaleOrdinal<string, string>;
}
export type Point = [number, number, number];
export interface Room {
	id: string;
	name: string;
	points: [number, number][];
	color?: string | null;
}
export interface Floor {
	id: string;
	name: string;
	elevation: number;
	bounds: [Point, Point];
	rooms: Room[];
}
export interface Wisp {
	id: string;
	name: string;
	externalId?: string | null;
	point: Point;
	floors: string[];
	orientation?: Point | null;
}
export interface Source {
	id: string;
	name: string;
	apLabel?: string | null;
	radioId?: string | null;
	bssid?: string | null;
	channel?: number | null;
	point: Point;
	floors: string[];
	orientation?: Point | null;
}
export interface SensingLink {
	id: string;
	name: string;
	wispId: string;
	sourceId: string;
}
export interface Config {
	schemaVersion: number;
	name: string;
	map: { flipX: boolean; flipY: boolean; wallThickness: number; wallColor?: string | null; wallOpacity: number | null };
	floors: Floor[];
	wisps: Wisp[];
	sources: Source[];
	links: SensingLink[];
}
export interface ConfigResponse {
	revision: string;
	config: Config;
	yaml: string;
	error?: string | null;
}
export interface MotionDiagnostic {
	wispId: string;
	sourceId?: string | null;
	linkId?: string | null;
	receivedAt: string;
	acquiredAt?: string | null;
	score?: number | null;
	motion?: boolean | null;
	ready?: boolean | null;
	provenance: { origin: string; adapter: string; referenceOrigin: string; radioOrigin: string };
	clockBasis?: string | null;
	configRevision: string;
	freshness: string;
}
export interface AppState {
	schemaVersion: number;
	configRevision: string;
	configurationError?: string | null;
	mqtt: { status: string; testInputEnabled: boolean };
	diagnostics: { accepted: number; rejected: number; dropped: number; stale: number; duplicate: number; outOfOrder: number; retained: number; lastRejection?: string | null; lastReceivedAt?: string | null; latest: MotionDiagnostic[] };
	medium: { schemaVersion: number; availability: string; inputHealth: string; rooms: { roomId: string; state: string }[]; tracks: null };
	demoEnabled: boolean;
}
