import {
  forwardRef,
  useEffect,
  useImperativeHandle,
  useRef,
} from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import {
  getBuildingTypeColor,
  getDistrictColor,
  getElevationColor,
  getHeightColor,
  getPoiMeta,
  getRoadSpeedColor,
} from '../constants/palette';
import {
  MapSpatialIndex,
  matchesBuildingFilter,
  matchesPoiFilter,
  matchesRegionFilter,
  matchesRoadFilter,
  matchesSearchQuery,
} from '../data/spatialIndex';
import type {
  CameraSyncState,
  CursorTelemetry,
  FilterState,
  FlyTarget,
  LayerVisibility,
  MapViewportRef,
  RegionFeature,
  SelectedFeature,
  ThreeSettings,
} from '../types/map';

interface ThreeMapViewProps {
  spatialIndex: MapSpatialIndex;
  visibility: LayerVisibility;
  filters: FilterState;
  threeSettings: ThreeSettings;
  selected: SelectedFeature | null;
  onSelect: (sel: SelectedFeature | null) => void;
  cameraSyncRef: React.MutableRefObject<CameraSyncState>;
  onTelemetry: (telemetry: CursorTelemetry) => void;
  pendingFlyTarget: FlyTarget | null;
}

const DEFAULT_VIEW_DIR = new THREE.Vector3(0.32, 0.72, 0.61).normalize();

function distanceForZoom2D(zoom2d: number, viewportHeight: number): number {
  const h = Math.max(400, viewportHeight);
  const d = h / (Math.pow(2, zoom2d) * 0.9326);
  return Math.min(12000, Math.max(110, d));
}

function zoom2DForDistance(distance: number, viewportHeight: number): number {
  const h = Math.max(400, viewportHeight);
  const z = Math.log2(h / (Math.max(80, distance) * 0.9326));
  return Math.min(3.5, Math.max(-4.5, z));
}

function disposeObject3D(obj: THREE.Object3D): void {
  obj.traverse((child) => {
    if (
      child instanceof THREE.Mesh ||
      child instanceof THREE.Line ||
      child instanceof THREE.LineSegments ||
      child instanceof THREE.LineLoop
    ) {
      child.geometry.dispose();
      if (Array.isArray(child.material)) {
        for (const m of child.material) m.dispose();
      } else {
        child.material.dispose();
      }
    }
  });
}

export const ThreeMapView = forwardRef<MapViewportRef, ThreeMapViewProps>(function ThreeMapView(
  {
    spatialIndex,
    visibility,
    filters,
    threeSettings,
    selected,
    onSelect,
    cameraSyncRef,
    onTelemetry,
    pendingFlyTarget,
  },
  ref,
) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const labelLayerRef = useRef<HTMLDivElement | null>(null);

  // Keep latest props accessible inside Three.js event callbacks & animation loop
  const propsRef = useRef({
    spatialIndex,
    visibility,
    filters,
    threeSettings,
    selected,
    onSelect,
    onTelemetry,
  });
  propsRef.current = {
    spatialIndex,
    visibility,
    filters,
    threeSettings,
    selected,
    onSelect,
    onTelemetry,
  };

  // Internal scene handles
  const sceneCtxRef = useRef<{
    scene: THREE.Scene;
    camera: THREE.PerspectiveCamera;
    renderer: THREE.WebGLRenderer;
    controls: OrbitControls;
    grid: THREE.GridHelper;
    buildingsMesh: THREE.InstancedMesh;
    buildingsMaterial: THREE.MeshStandardMaterial;
    roadsGroup: THREE.Group;
    roadsLines: THREE.LineSegments;
    roadsBasePos: Float32Array;
    roadsSegmentFeatureIdx: Int32Array;
    regionsGroup: THREE.Group;
    poiGroup: THREE.Group;
    poiMesh: THREE.InstancedMesh;
    poiStems: THREE.LineSegments;
    selectionGroup: THREE.Group;
    pulseRing: THREE.Mesh | null;
    labelItems: { region: RegionFeature; el: HTMLDivElement }[];
    flyAnim: {
      active: boolean;
      startTime: number;
      duration: number;
      fromTarget: THREE.Vector3;
      toTarget: THREE.Vector3;
      fromPos: THREE.Vector3;
      toPos: THREE.Vector3;
    };
    triggerFlyTo: (x: number, y: number, z?: number, distance?: number) => void;
    triggerFitAll: () => void;
  } | null>(null);

  useImperativeHandle(ref, () => ({
    flyTo(target) {
      sceneCtxRef.current?.triggerFlyTo(target.x, target.y, target.z, target.distance3d);
    },
    fitAll() {
      sceneCtxRef.current?.triggerFitAll();
    },
  }));

  // Build scene once per spatialIndex (dataset)
  useEffect(() => {
    const container = containerRef.current;
    const labelLayer = labelLayerRef.current;
    if (!container || !labelLayer) return;

    const { dataset } = spatialIndex;
    const width = Math.max(1, container.clientWidth);
    const height = Math.max(1, container.clientHeight);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color('#050811');
    const fog = new THREE.FogExp2(0x050811, 0.000095);
    scene.fog = propsRef.current.threeSettings.showFog ? fog : null;

    const camera = new THREE.PerspectiveCamera(50, width / height, 4, 38000);

    const renderer = new THREE.WebGLRenderer({
      antialias: true,
      powerPreference: 'high-performance',
    });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.setSize(width, height);
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.15;
    container.appendChild(renderer.domElement);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.085;
    controls.maxPolarAngle = Math.PI / 2 - 0.03;
    controls.minDistance = 40;
    controls.maxDistance = 15000;
    controls.screenSpacePanning = false;
    controls.autoRotate = propsRef.current.threeSettings.autoOrbit;
    controls.autoRotateSpeed = 0.45;

    // Initial position synced from cameraSyncRef
    const initSync = cameraSyncRef.current;
    const initDist = distanceForZoom2D(initSync.zoom2d, height);
    const initTarget = new THREE.Vector3(initSync.x, 15, -initSync.y);
    controls.target.copy(initTarget);
    camera.position.copy(initTarget).addScaledVector(DEFAULT_VIEW_DIR, initDist);
    controls.update();

    // Lighting
    const hemi = new THREE.HemisphereLight(0x38bdf8, 0x0b1120, 1.05);
    scene.add(hemi);

    const sunLight = new THREE.DirectionalLight(0xe0f2fe, 1.45);
    sunLight.position.set(3200, 4800, 2600);
    scene.add(sunLight);

    const rimLight = new THREE.DirectionalLight(0xff2a6d, 0.55);
    rimLight.position.set(-3800, 2200, -3200);
    scene.add(rimLight);

    // Tactical grid
    const grid = new THREE.GridHelper(16000, 64, 0x00f0ff, 0x132036);
    grid.position.y = -2;
    const gridMats = Array.isArray(grid.material) ? grid.material : [grid.material];
    for (const gm of gridMats) {
      gm.transparent = true;
      gm.opacity = 0.34;
    }
    grid.visible = propsRef.current.threeSettings.showGrid;
    scene.add(grid);

    // --- 1. Buildings InstancedMesh ---
    const bCount = Math.max(1, dataset.buildings.length);
    const boxGeom = new THREE.BoxGeometry(1, 1, 1);
    const buildingsMaterial = new THREE.MeshStandardMaterial({
      roughness: 0.48,
      metalness: 0.25,
    });
    const buildingsMesh = new THREE.InstancedMesh(boxGeom, buildingsMaterial, bCount);
    buildingsMesh.count = dataset.buildings.length;
    buildingsMesh.frustumCulled = false;

    const u = new THREE.Vector3();
    const v = new THREE.Vector3();
    const w = new THREE.Vector3();
    const m = new THREE.Matrix4();
    const tmpColor = new THREE.Color();

    for (let i = 0; i < dataset.buildings.length; i++) {
      const b = dataset.buildings[i];
      const ring = b.geometry.coordinates[0];
      const p0 = ring[0];
      const p1 = ring[1];
      const p2 = ring[2];
      const p3 = ring[3];

      u.set(p1[0] - p0[0], 0, -(p1[1] - p0[1]));
      v.set(p3[0] - p0[0], 0, -(p3[1] - p0[1]));
      if (u.lengthSq() < 0.01) u.set(1, 0, 0);
      if (v.lengthSq() < 0.01) v.set(0, 0, 1);

      const h = Math.max(0.8, b.properties.top_z - b.properties.base_z);
      w.set(0, h, 0);

      m.makeBasis(u, w, v);
      if (m.determinant() < 0) {
        v.negate();
        m.makeBasis(u, w, v);
      }
      m.setPosition(
        (p0[0] + p2[0]) * 0.5,
        (b.properties.base_z + b.properties.top_z) * 0.5,
        -(p0[1] + p2[1]) * 0.5,
      );
      buildingsMesh.setMatrixAt(i, m);
      tmpColor.set(getBuildingTypeColor(b._type));
      buildingsMesh.setColorAt(i, tmpColor);
    }
    buildingsMesh.instanceMatrix.needsUpdate = true;
    if (buildingsMesh.instanceColor) buildingsMesh.instanceColor.needsUpdate = true;
    buildingsMesh.scale.y = propsRef.current.threeSettings.zScale;
    buildingsMesh.computeBoundingSphere();
    scene.add(buildingsMesh);

    // --- 2. Roads LineSegments ---
    const roadsGroup = new THREE.Group();
    let totalSegs = 0;
    for (let i = 0; i < dataset.roads.length; i++) {
      totalSegs += Math.max(0, dataset.roads[i].geometry.coordinates.length - 1);
    }

    const roadsBasePos = new Float32Array(Math.max(1, totalSegs) * 6);
    const roadsPos = new Float32Array(Math.max(1, totalSegs) * 6);
    const roadsColors = new Float32Array(Math.max(1, totalSegs) * 6);
    const roadsSegmentFeatureIdx = new Int32Array(Math.max(1, totalSegs));

    let segCursor = 0;
    for (let i = 0; i < dataset.roads.length; i++) {
      const r = dataset.roads[i];
      const pts = r.geometry.coordinates;
      tmpColor.set(getRoadSpeedColor(r.properties.max_speed));

      for (let j = 0; j < pts.length - 1; j++) {
        const a = pts[j];
        const b = pts[j + 1];
        const off = segCursor * 6;
        roadsBasePos[off] = a[0];
        roadsBasePos[off + 1] = a[2] + 0.6;
        roadsBasePos[off + 2] = -a[1];
        roadsBasePos[off + 3] = b[0];
        roadsBasePos[off + 4] = b[2] + 0.6;
        roadsBasePos[off + 5] = -b[1];

        roadsColors[off] = tmpColor.r;
        roadsColors[off + 1] = tmpColor.g;
        roadsColors[off + 2] = tmpColor.b;
        roadsColors[off + 3] = tmpColor.r;
        roadsColors[off + 4] = tmpColor.g;
        roadsColors[off + 5] = tmpColor.b;

        roadsSegmentFeatureIdx[segCursor] = i;
        segCursor++;
      }
    }
    roadsPos.set(roadsBasePos);

    const roadsGeom = new THREE.BufferGeometry();
    roadsGeom.setAttribute('position', new THREE.BufferAttribute(roadsPos, 3));
    roadsGeom.setAttribute('color', new THREE.BufferAttribute(roadsColors, 3));
    const roadsMat = new THREE.LineBasicMaterial({
      vertexColors: true,
      transparent: true,
      opacity: 0.72,
    });
    const roadsLines = new THREE.LineSegments(roadsGeom, roadsMat);
    roadsLines.frustumCulled = false;
    roadsGroup.add(roadsLines);
    roadsGroup.scale.y = propsRef.current.threeSettings.zScale;
    scene.add(roadsGroup);

    // --- 3. Regions Group & DOM Labels ---
    const regionsGroup = new THREE.Group();
    scene.add(regionsGroup);

    // --- 4. POIs InstancedMesh + Elevation Stems ---
    const poiGroup = new THREE.Group();
    const pCount = Math.max(1, dataset.poi.length);
    const poiGeom = new THREE.OctahedronGeometry(13, 0);
    const poiMat = new THREE.MeshBasicMaterial();
    const poiMesh = new THREE.InstancedMesh(poiGeom, poiMat, pCount);
    poiMesh.count = dataset.poi.length;
    poiMesh.frustumCulled = false;

    const stemPos = new Float32Array(pCount * 6);
    const stemColors = new Float32Array(pCount * 6);
    const dummyObj = new THREE.Object3D();

    for (let i = 0; i < dataset.poi.length; i++) {
      const p = dataset.poi[i];
      const [px, py, pz = 0] = p.geometry.coordinates;
      const zScale = propsRef.current.threeSettings.zScale;
      dummyObj.position.set(px, pz * zScale + 18, -py);
      dummyObj.scale.set(1, 1.35, 1);
      dummyObj.updateMatrix();
      poiMesh.setMatrixAt(i, dummyObj.matrix);

      const meta = getPoiMeta(p.properties.category);
      tmpColor.set(meta.color);
      poiMesh.setColorAt(i, tmpColor);

      const off = i * 6;
      stemPos[off] = px;
      stemPos[off + 1] = 0;
      stemPos[off + 2] = -py;
      stemPos[off + 3] = px;
      stemPos[off + 4] = pz + 16 / Math.max(0.2, zScale);
      stemPos[off + 5] = -py;

      stemColors[off] = tmpColor.r * 0.25;
      stemColors[off + 1] = tmpColor.g * 0.25;
      stemColors[off + 2] = tmpColor.b * 0.25;
      stemColors[off + 3] = tmpColor.r;
      stemColors[off + 4] = tmpColor.g;
      stemColors[off + 5] = tmpColor.b;
    }
    poiMesh.instanceMatrix.needsUpdate = true;
    if (poiMesh.instanceColor) poiMesh.instanceColor.needsUpdate = true;
    poiMesh.computeBoundingSphere();

    const stemsGeom = new THREE.BufferGeometry();
    stemsGeom.setAttribute('position', new THREE.BufferAttribute(stemPos, 3));
    stemsGeom.setAttribute('color', new THREE.BufferAttribute(stemColors, 3));
    const stemsMat = new THREE.LineBasicMaterial({
      vertexColors: true,
      transparent: true,
      opacity: 0.45,
    });
    const poiStems = new THREE.LineSegments(stemsGeom, stemsMat);
    poiStems.frustumCulled = false;
    poiStems.scale.y = propsRef.current.threeSettings.zScale;

    poiGroup.add(poiStems);
    poiGroup.add(poiMesh);
    scene.add(poiGroup);

    // --- 5. Selection Highlight Group ---
    const selectionGroup = new THREE.Group();
    scene.add(selectionGroup);

    const flyAnim = {
      active: false,
      startTime: 0,
      duration: 550,
      fromTarget: new THREE.Vector3(),
      toTarget: new THREE.Vector3(),
      fromPos: new THREE.Vector3(),
      toPos: new THREE.Vector3(),
    };

    const triggerFlyTo = (x: number, y: number, z = 12, distance?: number) => {
      const zScale = propsRef.current.threeSettings.zScale;
      const toTarget = new THREE.Vector3(x, z * zScale, -y);
      const curDir = camera.position.clone().sub(controls.target);
      const currentDist = Math.max(80, curDir.length());
      if (currentDist > 0.001) curDir.normalize();
      else curDir.copy(DEFAULT_VIEW_DIR);

      const finalDist = distance ?? Math.min(currentDist, 1400);
      const toPos = toTarget.clone().addScaledVector(curDir, finalDist);

      flyAnim.fromTarget.copy(controls.target);
      flyAnim.toTarget.copy(toTarget);
      flyAnim.fromPos.copy(camera.position);
      flyAnim.toPos.copy(toPos);
      flyAnim.startTime = performance.now();
      flyAnim.duration = 550;
      flyAnim.active = true;
    };

    const triggerFitAll = () => {
      const [minX, minY, maxX, maxY] = spatialIndex.worldBBox;
      const cx = (minX + maxX) * 0.5;
      const cy = (minY + maxY) * 0.5;
      const span = Math.max(maxX - minX, maxY - minY);
      triggerFlyTo(cx, cy, 10, Math.min(11500, span * 0.72));
    };

    const labelItems: { region: RegionFeature; el: HTMLDivElement }[] = [];

    sceneCtxRef.current = {
      scene,
      camera,
      renderer,
      controls,
      grid,
      buildingsMesh,
      buildingsMaterial,
      roadsGroup,
      roadsLines,
      roadsBasePos,
      roadsSegmentFeatureIdx,
      regionsGroup,
      poiGroup,
      poiMesh,
      poiStems,
      selectionGroup,
      pulseRing: null,
      labelItems,
      flyAnim,
      triggerFlyTo,
      triggerFitAll,
    };

    // Cancel fly animation on manual user input
    const cancelFly = () => {
      flyAnim.active = false;
    };
    renderer.domElement.addEventListener('pointerdown', cancelFly);
    renderer.domElement.addEventListener('wheel', cancelFly, { passive: true });

    // Raycasting & Pointer Tracking
    const raycaster = new THREE.Raycaster();
    const pointerNdc = new THREE.Vector2();
    const groundPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
    const groundHit = new THREE.Vector3();

    let downX = 0;
    let downY = 0;
    let lastTelemetryTime = 0;

    const onPointerDown = (e: PointerEvent) => {
      downX = e.clientX;
      downY = e.clientY;
    };

    const onPointerMove = (e: PointerEvent) => {
      const now = performance.now();
      if (now - lastTelemetryTime < 65) return;
      lastTelemetryTime = now;

      const rect = renderer.domElement.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return;
      pointerNdc.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
      pointerNdc.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;

      raycaster.setFromCamera(pointerNdc, camera);
      if (raycaster.ray.intersectPlane(groundPlane, groundHit)) {
        const wx = groundHit.x;
        const wy = -groundHit.z;
        const loc = propsRef.current.spatialIndex.findDistrictAt(wx, wy);
        propsRef.current.onTelemetry({
          x: wx,
          y: wy,
          district: loc.district,
          subdistrict: loc.subdistrict,
        });
      }
    };

    const onPointerUp = (e: PointerEvent) => {
      if (e.button !== 0) return;
      const dist = Math.hypot(e.clientX - downX, e.clientY - downY);
      if (dist > 5) return; // Dragged camera

      const rect = renderer.domElement.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return;
      pointerNdc.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
      pointerNdc.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;

      raycaster.setFromCamera(pointerNdc, camera);

      const { visibility: vis, filters: flt, spatialIndex: si, onSelect: selectCb } = propsRef.current;

      // 1. Check POIs
      if (vis.poi) {
        const poiHits = raycaster.intersectObject(poiMesh, false);
        for (const hit of poiHits) {
          if (hit.instanceId !== undefined) {
            const p = si.dataset.poi[hit.instanceId];
            if (p && matchesPoiFilter(p, flt)) {
              selectCb({ kind: 'poi', feature: p });
              return;
            }
          }
        }
      }

      // 2. Check Buildings
      if (vis.buildings) {
        const bldHits = raycaster.intersectObject(buildingsMesh, false);
        for (const hit of bldHits) {
          if (hit.instanceId !== undefined) {
            const b = si.dataset.buildings[hit.instanceId];
            if (b && matchesBuildingFilter(b, flt)) {
              selectCb({ kind: 'building', feature: b });
              return;
            }
          }
        }
      }

      // 3. Fallback to ground plane 2D hit test for Roads & Regions
      if (raycaster.ray.intersectPlane(groundPlane, groundHit)) {
        const wx = groundHit.x;
        const wy = -groundHit.z;
        const camDist = camera.position.distanceTo(controls.target);
        const tol = Math.min(85, Math.max(10, camDist * 0.015));
        const res = si.hitTest2D(
          wx,
          wy,
          tol,
          { ...vis, poi: false, buildings: false },
          flt,
        );
        selectCb(res);
      } else {
        selectCb(null);
      }
    };

    renderer.domElement.addEventListener('pointerdown', onPointerDown);
    renderer.domElement.addEventListener('pointermove', onPointerMove);
    renderer.domElement.addEventListener('pointerup', onPointerUp);

    // Keyboard WASD / Arrow panning
    const pressedKeys = new Set<string>();
    const onKeyDown = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement | null)?.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
      const key = e.key.toLowerCase();
      if (['w', 'a', 's', 'd', 'arrowup', 'arrowdown', 'arrowleft', 'arrowright'].includes(key)) {
        pressedKeys.add(key);
      }
    };
    const onKeyUp = (e: KeyboardEvent) => {
      pressedKeys.delete(e.key.toLowerCase());
    };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);

    // ResizeObserver
    const resizeObserver = new ResizeObserver(() => {
      const wNew = Math.max(1, container.clientWidth);
      const hNew = Math.max(1, container.clientHeight);
      camera.aspect = wNew / hNew;
      camera.updateProjectionMatrix();
      renderer.setSize(wNew, hNew);
    });
    resizeObserver.observe(container);

    // Animation loop
    let animId = 0;
    let lastTime = performance.now();
    const projVec = new THREE.Vector3();
    const forwardVec = new THREE.Vector3();
    const rightVec = new THREE.Vector3();
    const panDelta = new THREE.Vector3();

    const animate = (now: number) => {
      animId = requestAnimationFrame(animate);
      const dt = Math.min(0.05, (now - lastTime) / 1000);
      lastTime = now;

      // Fly animation
      if (flyAnim.active) {
        const t = Math.min(1, (now - flyAnim.startTime) / flyAnim.duration);
        const ease = 1 - Math.pow(1 - t, 3);
        controls.target.lerpVectors(flyAnim.fromTarget, flyAnim.toTarget, ease);
        camera.position.lerpVectors(flyAnim.fromPos, flyAnim.toPos, ease);
        if (t >= 1) flyAnim.active = false;
      }

      // WASD / Arrow pan
      if (pressedKeys.size > 0) {
        flyAnim.active = false;
        camera.getWorldDirection(forwardVec);
        forwardVec.y = 0;
        if (forwardVec.lengthSq() > 0.0001) forwardVec.normalize();
        rightVec.crossVectors(forwardVec, camera.up).normalize();

        const camDist = camera.position.distanceTo(controls.target);
        const speed = Math.max(180, camDist * 0.95) * dt;
        panDelta.set(0, 0, 0);

        if (pressedKeys.has('w') || pressedKeys.has('arrowup')) panDelta.addScaledVector(forwardVec, speed);
        if (pressedKeys.has('s') || pressedKeys.has('arrowdown')) panDelta.addScaledVector(forwardVec, -speed);
        if (pressedKeys.has('d') || pressedKeys.has('arrowright')) panDelta.addScaledVector(rightVec, speed);
        if (pressedKeys.has('a') || pressedKeys.has('arrowleft')) panDelta.addScaledVector(rightVec, -speed);

        controls.target.add(panDelta);
        camera.position.add(panDelta);
      }

      controls.update();

      // Pulse selection ring if present
      const pulseRing = sceneCtxRef.current?.pulseRing;
      if (pulseRing) {
        const s = 1 + 0.25 * Math.sin(now * 0.006);
        pulseRing.scale.set(s, s, s);
      }

      renderer.render(scene, camera);

      // Sync camera state for 2D switcher
      const camDist = camera.position.distanceTo(controls.target);
      cameraSyncRef.current = {
        x: controls.target.x,
        y: -controls.target.z,
        zoom2d: zoom2DForDistance(camDist, container.clientHeight),
      };

      // Update projected 2D district labels via direct DOM mutation
      const ctx = sceneCtxRef.current;
      if (ctx) {
        const showLabels = propsRef.current.visibility.regions && propsRef.current.visibility.regionLabels;
        const cw = container.clientWidth;
        const ch = container.clientHeight;
        for (let i = 0; i < ctx.labelItems.length; i++) {
          const item = ctx.labelItems[i];
          const reg = item.region;
          if (!showLabels || (reg.properties.is_subdistrict && camDist > 3000)) {
            if (item.el.style.display !== 'none') item.el.style.display = 'none';
            continue;
          }
          projVec.set(reg.properties.center[0], 12, -reg.properties.center[1]);
          projVec.project(camera);
          if (projVec.z < -1 || projVec.z > 1) {
            if (item.el.style.display !== 'none') item.el.style.display = 'none';
            continue;
          }
          const sx = (projVec.x * 0.5 + 0.5) * cw;
          const sy = (-projVec.y * 0.5 + 0.5) * ch;
          if (sx < -120 || sx > cw + 120 || sy < -60 || sy > ch + 60) {
            if (item.el.style.display !== 'none') item.el.style.display = 'none';
            continue;
          }
          if (item.el.style.display !== 'block') item.el.style.display = 'block';
          item.el.style.transform = `translate3d(${sx.toFixed(1)}px, ${sy.toFixed(1)}px, 0) translate(-50%, -50%)`;
        }
      }
    };

    animId = requestAnimationFrame(animate);

    return () => {
      cancelAnimationFrame(animId);
      resizeObserver.disconnect();
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
      renderer.domElement.removeEventListener('pointerdown', cancelFly);
      renderer.domElement.removeEventListener('wheel', cancelFly);
      renderer.domElement.removeEventListener('pointerdown', onPointerDown);
      renderer.domElement.removeEventListener('pointermove', onPointerMove);
      renderer.domElement.removeEventListener('pointerup', onPointerUp);

      controls.dispose();
      disposeObject3D(regionsGroup);
      disposeObject3D(selectionGroup);
      boxGeom.dispose();
      buildingsMaterial.dispose();
      roadsGeom.dispose();
      roadsMat.dispose();
      poiGeom.dispose();
      poiMat.dispose();
      stemsGeom.dispose();
      stemsMat.dispose();
      grid.dispose();
      renderer.dispose();

      if (renderer.domElement.parentNode === container) {
        container.removeChild(renderer.domElement);
      }
      labelLayer.innerHTML = '';
      sceneCtxRef.current = null;
    };
  }, [spatialIndex, cameraSyncRef]);

  // Apply Three.js visual settings (grid, fog, zScale, buildingStyle, autoOrbit)
  useEffect(() => {
    const ctx = sceneCtxRef.current;
    if (!ctx) return;

    ctx.grid.visible = threeSettings.showGrid;
    ctx.controls.autoRotate = threeSettings.autoOrbit;

    const wantFog = threeSettings.showFog;
    const hadFog = ctx.scene.fog !== null;
    if (wantFog !== hadFog) {
      ctx.scene.fog = wantFog ? new THREE.FogExp2(0x050811, 0.000095) : null;
      ctx.scene.traverse((obj) => {
        if (
          obj instanceof THREE.Mesh ||
          obj instanceof THREE.Line ||
          obj instanceof THREE.LineSegments ||
          obj instanceof THREE.LineLoop
        ) {
          const mats = Array.isArray(obj.material) ? obj.material : [obj.material];
          for (const mat of mats) mat.needsUpdate = true;
        }
      });
    }

    const isHolo = threeSettings.buildingStyle === 'hologram';
    ctx.buildingsMaterial.wireframe = isHolo;
    ctx.buildingsMaterial.transparent = isHolo;
    ctx.buildingsMaterial.opacity = isHolo ? 0.58 : 1.0;
    ctx.buildingsMaterial.roughness = isHolo ? 0.25 : 0.48;
    ctx.buildingsMaterial.metalness = isHolo ? 0.65 : 0.25;
    ctx.buildingsMaterial.emissive.set(isHolo ? '#031d2c' : '#000000');
    ctx.buildingsMaterial.needsUpdate = true;

    ctx.buildingsMesh.scale.y = threeSettings.zScale;
    ctx.roadsGroup.scale.y = threeSettings.zScale;
    ctx.poiStems.scale.y = threeSettings.zScale;
  }, [threeSettings]);

  // Update Buildings visibility, filters, colors, search highlight
  useEffect(() => {
    const ctx = sceneCtxRef.current;
    if (!ctx) return;

    ctx.buildingsMesh.visible = visibility.buildings;
    if (!visibility.buildings) return;

    const buildings = spatialIndex.dataset.buildings;
    const u = new THREE.Vector3();
    const v = new THREE.Vector3();
    const w = new THREE.Vector3();
    const m = new THREE.Matrix4();
    const zeroMat = new THREE.Matrix4().makeScale(0, 0, 0);
    const tmpColor = new THREE.Color();
    const q = filters.searchQuery.trim();

    for (let i = 0; i < buildings.length; i++) {
      const b = buildings[i];
      if (!matchesBuildingFilter(b, filters)) {
        ctx.buildingsMesh.setMatrixAt(i, zeroMat);
        continue;
      }

      const ring = b.geometry.coordinates[0];
      const p0 = ring[0];
      const p1 = ring[1];
      const p2 = ring[2];
      const p3 = ring[3];

      u.set(p1[0] - p0[0], 0, -(p1[1] - p0[1]));
      v.set(p3[0] - p0[0], 0, -(p3[1] - p0[1]));
      if (u.lengthSq() < 0.01) u.set(1, 0, 0);
      if (v.lengthSq() < 0.01) v.set(0, 0, 1);

      const h = Math.max(0.8, b.properties.top_z - b.properties.base_z);
      w.set(0, h, 0);
      m.makeBasis(u, w, v);
      if (m.determinant() < 0) {
        v.negate();
        m.makeBasis(u, w, v);
      }
      m.setPosition(
        (p0[0] + p2[0]) * 0.5,
        (b.properties.base_z + b.properties.top_z) * 0.5,
        -(p0[1] + p2[1]) * 0.5,
      );
      ctx.buildingsMesh.setMatrixAt(i, m);

      const isMatch =
        q.length > 0 &&
        matchesSearchQuery(
          [b.properties.name, b._type, b.properties.district, b.properties.subdistrict],
          q,
        );

      if (isMatch) {
        tmpColor.set('#fcee09');
      } else if (filters.buildingColorMode === 'height') {
        tmpColor.set(getHeightColor(b.properties.height_m));
      } else if (filters.buildingColorMode === 'district') {
        tmpColor.set(getDistrictColor(b.properties.district));
      } else {
        tmpColor.set(getBuildingTypeColor(b._type));
      }
      ctx.buildingsMesh.setColorAt(i, tmpColor);
    }

    ctx.buildingsMesh.instanceMatrix.needsUpdate = true;
    if (ctx.buildingsMesh.instanceColor) ctx.buildingsMesh.instanceColor.needsUpdate = true;
    ctx.buildingsMesh.computeBoundingSphere();
  }, [
    spatialIndex,
    visibility.buildings,
    filters.district,
    filters.buildingTypes,
    filters.buildingColorMode,
    filters.searchQuery,
  ]);

  // Update Roads visibility, district filter, color mode
  useEffect(() => {
    const ctx = sceneCtxRef.current;
    if (!ctx) return;

    ctx.roadsGroup.visible = visibility.roads;
    if (!visibility.roads) return;

    const posAttr = ctx.roadsLines.geometry.getAttribute('position') as THREE.BufferAttribute;
    const colorAttr = ctx.roadsLines.geometry.getAttribute('color') as THREE.BufferAttribute;
    const posArr = posAttr.array as Float32Array;
    const colArr = colorAttr.array as Float32Array;
    const base = ctx.roadsBasePos;
    const roads = spatialIndex.dataset.roads;
    const tmpColor = new THREE.Color();

    let segIdx = 0;
    for (let i = 0; i < roads.length; i++) {
      const r = roads[i];
      const segCount = Math.max(0, r.geometry.coordinates.length - 1);
      const visible = matchesRoadFilter(r, filters);

      if (visible) {
        if (filters.roadColorMode === 'elevation') {
          const avgZ = r.geometry.coordinates[0]?.[2] ?? 0;
          tmpColor.set(getElevationColor(avgZ));
        } else if (filters.roadColorMode === 'district') {
          tmpColor.set(getDistrictColor(r.properties.district));
        } else {
          tmpColor.set(getRoadSpeedColor(r.properties.max_speed));
        }
      }

      for (let j = 0; j < segCount; j++) {
        const off = segIdx * 6;
        if (visible) {
          posArr[off] = base[off];
          posArr[off + 1] = base[off + 1];
          posArr[off + 2] = base[off + 2];
          posArr[off + 3] = base[off + 3];
          posArr[off + 4] = base[off + 4];
          posArr[off + 5] = base[off + 5];

          colArr[off] = tmpColor.r;
          colArr[off + 1] = tmpColor.g;
          colArr[off + 2] = tmpColor.b;
          colArr[off + 3] = tmpColor.r;
          colArr[off + 4] = tmpColor.g;
          colArr[off + 5] = tmpColor.b;
        } else {
          posArr[off] = 0;
          posArr[off + 1] = 0;
          posArr[off + 2] = 0;
          posArr[off + 3] = 0;
          posArr[off + 4] = 0;
          posArr[off + 5] = 0;
        }
        segIdx++;
      }
    }

    posAttr.needsUpdate = true;
    colorAttr.needsUpdate = true;
  }, [spatialIndex, visibility.roads, filters.district, filters.roadColorMode]);

  // Update POIs visibility, filters, zScale, search highlight
  useEffect(() => {
    const ctx = sceneCtxRef.current;
    if (!ctx) return;

    ctx.poiGroup.visible = visibility.poi;
    if (!visibility.poi) return;

    const pois = spatialIndex.dataset.poi;
    const dummy = new THREE.Object3D();
    const zeroMat = new THREE.Matrix4().makeScale(0, 0, 0);
    const tmpColor = new THREE.Color();
    const stemPosAttr = ctx.poiStems.geometry.getAttribute('position') as THREE.BufferAttribute;
    const stemArr = stemPosAttr.array as Float32Array;
    const zScale = threeSettings.zScale;
    const q = filters.searchQuery.trim();

    for (let i = 0; i < pois.length; i++) {
      const p = pois[i];
      const off = i * 6;
      if (!matchesPoiFilter(p, filters)) {
        ctx.poiMesh.setMatrixAt(i, zeroMat);
        stemArr[off] = 0;
        stemArr[off + 1] = 0;
        stemArr[off + 2] = 0;
        stemArr[off + 3] = 0;
        stemArr[off + 4] = 0;
        stemArr[off + 5] = 0;
        continue;
      }

      const [px, py, pz = 0] = p.geometry.coordinates;
      const isMatch =
        q.length > 0 &&
        matchesSearchQuery(
          [p.properties.name, getPoiMeta(p.properties.category).label, p.properties.district, p.properties.subdistrict],
          q,
        );

      const scaleMul = isMatch ? 1.85 : 1.0;
      dummy.position.set(px, pz * zScale + 18, -py);
      dummy.scale.set(scaleMul, 1.35 * scaleMul, scaleMul);
      dummy.updateMatrix();
      ctx.poiMesh.setMatrixAt(i, dummy.matrix);

      tmpColor.set(isMatch ? '#fcee09' : getPoiMeta(p.properties.category).color);
      ctx.poiMesh.setColorAt(i, tmpColor);

      stemArr[off] = px;
      stemArr[off + 1] = 0;
      stemArr[off + 2] = -py;
      stemArr[off + 3] = px;
      stemArr[off + 4] = pz + 16 / Math.max(0.2, zScale);
      stemArr[off + 5] = -py;
    }

    ctx.poiMesh.instanceMatrix.needsUpdate = true;
    if (ctx.poiMesh.instanceColor) ctx.poiMesh.instanceColor.needsUpdate = true;
    ctx.poiMesh.computeBoundingSphere();
    stemPosAttr.needsUpdate = true;
  }, [
    spatialIndex,
    visibility.poi,
    filters.district,
    filters.poiCategories,
    filters.searchQuery,
    threeSettings.zScale,
  ]);

  // Update Regions geometry & projected DOM labels
  useEffect(() => {
    const ctx = sceneCtxRef.current;
    const labelLayer = labelLayerRef.current;
    if (!ctx || !labelLayer) return;

    disposeObject3D(ctx.regionsGroup);
    ctx.regionsGroup.clear();
    labelLayer.innerHTML = '';
    ctx.labelItems.length = 0;

    ctx.regionsGroup.visible = visibility.regions;
    if (!visibility.regions) return;

    const linePositions: number[] = [];
    const lineColors: number[] = [];
    const tmpColor = new THREE.Color();
    const q = filters.searchQuery.trim();

    for (const reg of spatialIndex.dataset.regions) {
      if (!matchesRegionFilter(reg, visibility, filters)) continue;

      const ring = reg.geometry.coordinates[0];
      const isMatch =
        q.length > 0 &&
        matchesSearchQuery([reg.properties.name, reg.properties.top_district, reg.properties.district_id], q);

      const baseHex = isMatch ? '#fcee09' : getDistrictColor(reg.properties.top_district);
      tmpColor.set(baseHex);

      // Outline segments
      for (let i = 0; i < ring.length - 1; i++) {
        linePositions.push(ring[i][0], 2, -ring[i][1]);
        linePositions.push(ring[i + 1][0], 2, -ring[i + 1][1]);
        lineColors.push(tmpColor.r, tmpColor.g, tmpColor.b);
        lineColors.push(tmpColor.r, tmpColor.g, tmpColor.b);
      }

      // Subtle fill for top-level non-quest districts or matched regions
      if ((!reg.properties.is_subdistrict && !reg.properties.is_quest_district) || isMatch) {
        const shape = new THREE.Shape();
        for (let i = 0; i < ring.length; i++) {
          if (i === 0) shape.moveTo(ring[i][0], ring[i][1]);
          else shape.lineTo(ring[i][0], ring[i][1]);
        }
        const shapeGeom = new THREE.ShapeGeometry(shape);
        shapeGeom.rotateX(-Math.PI / 2);
        const shapeMat = new THREE.MeshBasicMaterial({
          color: baseHex,
          transparent: true,
          opacity: isMatch ? 0.16 : 0.055,
          depthWrite: false,
          side: THREE.DoubleSide,
        });
        const fillMesh = new THREE.Mesh(shapeGeom, shapeMat);
        fillMesh.position.y = 0.5;
        ctx.regionsGroup.add(fillMesh);
      }

      // Label DOM node
      if (!reg.properties.is_quest_district) {
        const el = document.createElement('div');
        el.className = reg.properties.is_subdistrict
          ? 'nc-map-label nc-map-label--sub'
          : 'nc-map-label nc-map-label--top';
        if (isMatch) el.classList.add('nc-map-label--match');
        el.textContent = reg.properties.name;
        el.style.display = 'none';
        labelLayer.appendChild(el);
        ctx.labelItems.push({ region: reg, el });
      }
    }

    if (linePositions.length > 0) {
      const borderGeom = new THREE.BufferGeometry();
      borderGeom.setAttribute('position', new THREE.Float32BufferAttribute(linePositions, 3));
      borderGeom.setAttribute('color', new THREE.Float32BufferAttribute(lineColors, 3));
      const borderMat = new THREE.LineBasicMaterial({
        vertexColors: true,
        transparent: true,
        opacity: 0.78,
      });
      const borders = new THREE.LineSegments(borderGeom, borderMat);
      borders.frustumCulled = false;
      ctx.regionsGroup.add(borders);
    }
  }, [
    spatialIndex,
    visibility.regions,
    visibility.regionSubdistricts,
    visibility.regionQuest,
    filters.district,
    filters.searchQuery,
  ]);

  // Update Selection Highlight Group
  useEffect(() => {
    const ctx = sceneCtxRef.current;
    if (!ctx) return;

    disposeObject3D(ctx.selectionGroup);
    ctx.selectionGroup.clear();
    ctx.pulseRing = null;

    if (!selected) return;
    const zScale = threeSettings.zScale;

    if (selected.kind === 'building') {
      const b = selected.feature;
      const ring = b.geometry.coordinates[0];
      const p0 = ring[0];
      const p1 = ring[1];
      const p2 = ring[2];
      const p3 = ring[3];

      const u = new THREE.Vector3(p1[0] - p0[0], 0, -(p1[1] - p0[1]));
      const v = new THREE.Vector3(p3[0] - p0[0], 0, -(p3[1] - p0[1]));
      const h = Math.max(1.2, (b.properties.top_z - b.properties.base_z) * zScale);
      const w = new THREE.Vector3(0, h, 0);

      const m = new THREE.Matrix4().makeBasis(u, w, v);
      if (m.determinant() < 0) {
        v.negate();
        m.makeBasis(u, w, v);
      }
      m.setPosition(
        (p0[0] + p2[0]) * 0.5,
        (b.properties.base_z + b.properties.top_z) * 0.5 * zScale,
        -(p0[1] + p2[1]) * 0.5,
      );

      const unitBox = new THREE.BoxGeometry(1.03, 1.02, 1.03);
      const fillMat = new THREE.MeshBasicMaterial({
        color: '#00f0ff',
        transparent: true,
        opacity: 0.22,
        depthWrite: false,
      });
      const fill = new THREE.Mesh(unitBox, fillMat);
      fill.applyMatrix4(m);
      ctx.selectionGroup.add(fill);

      const edges = new THREE.EdgesGeometry(unitBox);
      const edgeMat = new THREE.LineBasicMaterial({ color: '#00f0ff' });
      const wire = new THREE.LineSegments(edges, edgeMat);
      wire.applyMatrix4(m);
      ctx.selectionGroup.add(wire);
    } else if (selected.kind === 'road') {
      const road = selected.feature;
      const addLaneLine = (pts: [number, number, number][], color: string, yOffset: number) => {
        const vecs = pts.map((pt) => new THREE.Vector3(pt[0], pt[2] * zScale + yOffset, -pt[1]));
        const g = new THREE.BufferGeometry().setFromPoints(vecs);
        const mat = new THREE.LineBasicMaterial({ color });
        ctx.selectionGroup.add(new THREE.Line(g, mat));
      };

      addLaneLine(road.geometry.coordinates, '#00f0ff', 1.6);

      // Direction arrow cone near the middle of the selected lane
      const pts = road.geometry.coordinates;
      if (pts.length >= 2) {
        const midIdx = Math.floor((pts.length - 1) / 2);
        const a = pts[midIdx];
        const b = pts[midIdx + 1];
        const dir = new THREE.Vector3(b[0] - a[0], (b[2] - a[2]) * zScale, -(b[1] - a[1]));
        if (dir.lengthSq() > 0.01) {
          dir.normalize();
          const coneGeom = new THREE.ConeGeometry(4.5, 13, 8);
          const coneMat = new THREE.MeshBasicMaterial({ color: '#fcee09' });
          const cone = new THREE.Mesh(coneGeom, coneMat);
          cone.position.set(
            (a[0] + b[0]) * 0.5,
            ((a[2] + b[2]) * 0.5) * zScale + 3.5,
            -((a[1] + b[1]) * 0.5),
          );
          cone.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir);
          ctx.selectionGroup.add(cone);
        }
      }

      for (const inId of road.properties.in_lanes) {
        const lane = spatialIndex.roadById.get(inId);
        if (lane) addLaneLine(lane.geometry.coordinates, '#22c55e', 1.4);
      }
      for (const outId of road.properties.out_lanes) {
        const lane = spatialIndex.roadById.get(outId);
        if (lane) addLaneLine(lane.geometry.coordinates, '#ff2a6d', 1.4);
      }
    } else if (selected.kind === 'poi') {
      const [px, py, pz = 0] = selected.feature.geometry.coordinates;
      const ringGeom = new THREE.RingGeometry(22, 30, 32);
      ringGeom.rotateX(-Math.PI / 2);
      const ringMat = new THREE.MeshBasicMaterial({
        color: '#fcee09',
        side: THREE.DoubleSide,
        transparent: true,
        opacity: 0.9,
      });
      const ring = new THREE.Mesh(ringGeom, ringMat);
      ring.position.set(px, pz * zScale + 18, -py);
      ctx.selectionGroup.add(ring);
      ctx.pulseRing = ring;

      const beamGeom = new THREE.BufferGeometry().setFromPoints([
        new THREE.Vector3(px, 0, -py),
        new THREE.Vector3(px, pz * zScale + 180, -py),
      ]);
      const beamMat = new THREE.LineBasicMaterial({
        color: '#fcee09',
        transparent: true,
        opacity: 0.65,
      });
      ctx.selectionGroup.add(new THREE.Line(beamGeom, beamMat));
    } else if (selected.kind === 'region') {
      const ring = selected.feature.geometry.coordinates[0];
      const pts = ring.map((p) => new THREE.Vector3(p[0], 5, -p[1]));
      const g = new THREE.BufferGeometry().setFromPoints(pts);
      const mat = new THREE.LineBasicMaterial({ color: '#fcee09' });
      ctx.selectionGroup.add(new THREE.LineLoop(g, mat));
    }
  }, [selected, spatialIndex, threeSettings.zScale]);

  // Execute pending fly target when triggered externally
  useEffect(() => {
    if (!pendingFlyTarget) return;
    sceneCtxRef.current?.triggerFlyTo(
      pendingFlyTarget.x,
      pendingFlyTarget.y,
      pendingFlyTarget.z,
      pendingFlyTarget.distance3d,
    );
  }, [pendingFlyTarget]);

  return (
    <div className="viewport-wrapper">
      <div ref={containerRef} className="viewport-canvas" />
      <div ref={labelLayerRef} className="nc-label-overlay" />
    </div>
  );
});
