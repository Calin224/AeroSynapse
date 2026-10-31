import { BoxGeometry, Group, Mesh, MeshStandardMaterial } from 'three';
import { HeatmapLayer } from './heatmap-layer';
import { buildHeatmapFrame } from './heatmap-model';
import { datasetFixture } from './heatmap.testing';
import { TERMINAL_MAP_ZONES } from './terminal-zones';

describe('3D congestion mapping', () => {
  it('maps exact zone IDs and reuses surfaces/materials while keeping PBR materials unchanged', () => {
    const asset = new Group(),
      zone = new Group();
    zone.userData = { clickable: true, ...TERMINAL_MAP_ZONES[0] };
    const original = new MeshStandardMaterial({ color: 0xffffff });
    const equipment = new Mesh(new BoxGeometry(), original);
    zone.add(equipment);
    asset.add(zone);
    const layer = new HeatmapLayer();
    layer.register(asset);
    const dataset = datasetFixture();
    layer.apply(buildHeatmapFrame(dataset, 'baseline', 2));
    const first = layer.group.children[0] as Mesh;
    const red = first.material,
      geometry = first.geometry;
    expect(first.userData).toMatchObject({
      zoneId: zone.userData['zoneId'],
      runId: 'base-run',
      minute: 2,
      band: 'critical',
    });
    layer.apply(buildHeatmapFrame(dataset, 'optimized', 2));
    expect(layer.group.children[0]).toBe(first);
    expect(first.geometry).toBe(geometry);
    expect(first.material).not.toBe(red);
    layer.apply(buildHeatmapFrame(dataset, 'baseline', 2));
    expect(first.material).toBe(red);
    expect(equipment.material).toBe(original);
    layer.apply(null);
    expect(first.visible).toBe(false);
    const release = vi.spyOn(geometry, 'dispose');
    layer.dispose();
    expect(release).toHaveBeenCalledOnce();
  });
  it('logs an unknown ID once per run and still renders the known snapshot', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const asset = new Group(),
      zone = new Group();
    zone.userData = { clickable: true, ...TERMINAL_MAP_ZONES[0] };
    asset.add(zone);
    const layer = new HeatmapLayer();
    layer.register(asset);
    const dataset = datasetFixture();
    dataset.baseline.snapshots.push({
      zoneId: 'unknown-zone',
      elapsedMinutes: 2,
      queuedPassengers: 10,
      processedPassengers: 1,
    });
    const frame = buildHeatmapFrame(dataset, 'baseline', 2);
    expect(() => {
      layer.apply(frame);
      layer.apply(frame);
    }).not.toThrow();
    expect(layer.group.children).toHaveLength(1);
    expect(warn).toHaveBeenCalledTimes(1);
    expect(warn).toHaveBeenCalledWith('ZoneSnapshot fără mesh', {
      zoneId: 'unknown-zone',
      runId: 'base-run',
      minute: 2,
    });
    layer.dispose();
  });
});
