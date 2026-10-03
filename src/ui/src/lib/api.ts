import type { Config, ConfigResponse } from './types';
export async function request<T>(path: string, init?: RequestInit): Promise<T> {
	const response = await fetch(path, { signal: AbortSignal.timeout(8000), ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } });
	if (!response.ok) {
		// Never echo an arbitrary server response body (it may contain raw submitted YAML).
		let message = response.status === 409 ? 'Configuration changed. Reload the current configuration before saving.' : `Request failed (${response.status}). Check references, IDs, geometry and field values.`;
		if (response.status === 404) message = 'This operation is unavailable.';
		throw new Error(message);
	}
	return response.json() as Promise<T>;
}
export const loadConfig = () => request<ConfigResponse>('/api/config');
export const saveConfig = (revision: string, config: Config) => request<ConfigResponse>('/api/config', { method: 'PUT', body: JSON.stringify({ revision, config }) });
export const saveYaml = (revision: string, yaml: string) => request<ConfigResponse>('/api/config/yaml', { method: 'PUT', body: JSON.stringify({ revision, yaml }) });
