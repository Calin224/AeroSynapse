import {
  BoxGeometry,
  ConeGeometry,
  Group,
  Mesh,
  MeshStandardMaterial,
  SphereGeometry,
} from 'three';
import { TwinAircraft } from './twin-model';

function box(
  parent: Group,
  size: [number, number, number],
  position: [number, number, number],
  color: number,
) {
  const mesh = new Mesh(
    new BoxGeometry(...size),
    new MeshStandardMaterial({ color, roughness: 0.75 }),
  );
  mesh.position.set(...position);
  parent.add(mesh);
  return mesh;
}

export function createAirport(): Group {
  const airport = new Group();
  box(airport, [100, 0.4, 65], [0, -0.3, 0], 0x18332e);
  box(airport, [92, 0.1, 8], [0, 0, -22], 0x354451);
  box(airport, [72, 0.1, 4], [0, 0, -7], 0x354451);
  box(airport, [50, 0.1, 18], [0, 0, 8], 0x43535f);
  for (let x = -40; x <= 40; x += 8) box(airport, [3, 0.04, 0.25], [x, 0.1, -22], 0xdce8e4);
  for (const x of [-42, 42])
    for (let z = -24; z <= -20; z += 1) box(airport, [4, 0.04, 0.4], [x, 0.1, z], 0xdce8e4);
  for (const x of [-30, 30]) box(airport, [4, 0.1, 15], [x, 0.01, -14], 0x354451);
  for (let x = -18; x <= 18; x += 12) {
    box(airport, [0.18, 0.04, 11], [x, 0.1, 9], 0xe9c770);
    box(airport, [3, 1.3, 1.2], [x, 1.7, 17], 0x80a6b2);
  }
  box(airport, [48, 5, 7], [0, 2.5, 22], 0x658696);
  box(airport, [50, 0.5, 9], [0, 5.2, 22], 0xa8c2cd);
  box(airport, [46, 1.5, 0.15], [0, 3, 18.4], 0x203e50);
  for (let x = -21; x <= 21; x += 3) box(airport, [0.1, 1.5, 0.25], [x, 3, 18.3], 0x86b5bd);
  box(airport, [3, 9, 3], [-36, 4.5, 19], 0x6b8793);
  box(airport, [6, 2.5, 5], [-36, 10, 19], 0x82d8d0);
  box(airport, [7, 0.5, 6], [-36, 11.5, 19], 0xa8c2cd);
  box(airport, [12, 5, 9], [37, 2.5, 20], 0x426373);
  return airport;
}

export function createAircraft(data: TwinAircraft): Group {
  const aircraft = new Group();
  aircraft.name = data.id;
  const material = new MeshStandardMaterial({ color: 0xe8f2f4, roughness: 0.45 });
  const fuselage = new Mesh(new SphereGeometry(1, 16, 12), material);
  fuselage.scale.set(0.55, 0.55, 3.1);
  aircraft.add(fuselage);
  box(aircraft, [6, 0.13, 1.3], [0, 0, 0.2], 0xc0d9df);
  box(aircraft, [2.4, 0.1, 0.7], [0, 0.2, 2.1], 0x68c9b5);
  const tail = new Mesh(
    new ConeGeometry(0.55, 1.6, 3),
    new MeshStandardMaterial({ color: 0x59d9ba }),
  );
  tail.position.set(0, 0.7, 2.1);
  aircraft.add(tail);
  aircraft.position.set(data.x, 1, data.z);
  aircraft.rotation.y = (data.heading * Math.PI) / 180;
  return aircraft;
}
