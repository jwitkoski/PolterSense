<script lang="ts">
	let backgroundImage: string | null = $state(null);
	let fileInput: HTMLInputElement | undefined = $state();
	let rotation = $state(0);
	let error = $state('');
	function upload(event: Event) {
		const file = (event.target as HTMLInputElement).files?.[0];
		if (!file) return;
		if (file.size > 8 * 1024 * 1024 || !['image/png', 'image/jpeg', 'image/webp', 'image/svg+xml'].includes(file.type)) {
			error = 'Choose a PNG, JPEG, WebP or SVG under 8 MB.';
			return;
		}
		error = '';
		const reader = new FileReader();
		reader.onload = () => {
			backgroundImage = reader.result as string;
		};
		reader.readAsDataURL(file);
	}
</script>

{#if backgroundImage}<div class="map-background"><img src={backgroundImage} alt="Local floor plan reference" style={`transform:rotate(${rotation}deg)`} /></div>{/if}
<div class="background-controls">
	<input bind:this={fileInput} type="file" accept="image/png,image/jpeg,image/webp,image/svg+xml,.svg" onchange={upload} aria-label="Choose local floor plan background" hidden />
	<button onclick={() => fileInput?.click()}>Local background</button>
	{#if backgroundImage}<button onclick={() => (rotation = (rotation + 90) % 360)}>Rotate</button><button
			onclick={() => {
				backgroundImage = null;
				rotation = 0;
				if (fileInput) fileInput.value = '';
			}}>Clear</button
		>{/if}
	{#if error}<span role="alert">{error}</span>{/if}
</div>
