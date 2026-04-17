/**
 * GenericDevice — JSON Device Spec을 해석하는 범용 Lib3D 클래스
 *
 * 기존 29개 하드코딩 모델(Robot.js, Unit.js 등)과 동일한 인터페이스를 구현하여
 * Ds2View3DLibrary에 그대로 꽂히는 구조.
 *
 * 인터페이스:
 *   GenericDevice.create(THREE, options)          → THREE.Group
 *   GenericDevice.updateState(model, state)       → void
 *   GenericDevice.animate(model, direction, speed) → boolean
 *
 * options.spec: JSON 디바이스 스펙 객체 (DEVICE_JSON_SPEC.md 참조)
 */

class GenericDevice {

  // ════════════════════════════════════════════════════════════
  // Shared Utilities
  // ════════════════════════════════════════════════════════════

  static _gradientMap = null;

  static _getGradientMap(THREE) {
    if (!this._gradientMap) {
      const c = new Uint8Array([50, 180, 255]);
      this._gradientMap = new THREE.DataTexture(c, 3, 1, THREE.LuminanceFormat);
      this._gradientMap.needsUpdate = true;
    }
    return this._gradientMap;
  }

  static _createGeometry(THREE, part) {
    const s = part.shape;
    if (s === 'box') {
      const sz = part.size || [1,1,1];
      return new THREE.BoxGeometry(sz[0], sz[1], sz[2]);
    }
    if (s === 'cylinder') {
      let rT, rB;
      if (Array.isArray(part.radius)) { rT = part.radius[0]; rB = part.radius[1]; }
      else { rT = rB = part.radius || 0.5; }
      return new THREE.CylinderGeometry(rT, rB, part.height || 1, 24);
    }
    if (s === 'sphere') return new THREE.SphereGeometry(part.radius || 0.5, 20, 20);
    if (s === 'cone') return new THREE.ConeGeometry(part.radius || 0.5, part.height || 1, 20);
    return new THREE.BoxGeometry(1, 1, 1);
  }

  static _createMaterial(THREE, part) {
    const gm = this._getGradientMap(THREE);
    const color = new THREE.Color(part.color || '#94a3b8');
    const opts = { color, gradientMap: gm };
    const glow = part.glow || 0;
    if (glow > 0) { opts.emissive = color.clone(); opts.emissiveIntensity = glow; }
    if (part.opacity !== undefined && part.opacity < 1) { opts.transparent = true; opts.opacity = part.opacity; }
    return new THREE.MeshToonMaterial(opts);
  }

  static _addOutline(THREE, mesh, parent) {
    const outline = new THREE.Mesh(
      mesh.geometry.clone(),
      new THREE.MeshBasicMaterial({ color: 0x000000, side: THREE.BackSide })
    );
    outline.position.copy(mesh.position);
    outline.rotation.copy(mesh.rotation);
    outline.scale.copy(mesh.scale).multiplyScalar(1.08);
    outline.renderOrder = -1;
    parent.add(outline);
  }

  static _getHalfH(part) {
    if (part.shape === 'box') return (part.size ? part.size[1] : 1) / 2;
    if (part.shape === 'cylinder') return (part.height || 1) / 2;
    if (part.shape === 'sphere') return part.radius || 0.5;
    if (part.shape === 'cone') return (part.height || 1) / 2;
    return 0.5;
  }

  // ════════════════════════════════════════════════════════════
  // Named Poses (built-in)
  // ════════════════════════════════════════════════════════════

  static NAMED_POSES = {
    home:         { angles: [0, 0, 0, 0],         t: 0.8 },
    ready:        { angles: [0, -20, 30, 0],      t: 0.6 },
    tuck:         { angles: [0, -15, 95, 30],     t: 0.8 },
    extend_front: { angles: [0, -50, 15, -15],    t: 1.0 },
    reach_right:  { angles: [45, -30, 50, -10],   t: 1.0 },
    down_right:   { angles: [45, -55, 80, 30],    t: 0.8 },
    up_right:     { angles: [40, -10, 20, -15],   t: 0.8 },
    reach_left:   { angles: [-45, -30, 50, -10],  t: 1.0 },
    down_left:    { angles: [-45, -55, 80, 30],   t: 0.8 },
    up_left:      { angles: [-40, -10, 20, -15],  t: 0.8 },
    reach_front:  { angles: [0, -35, 55, -10],    t: 1.0 },
    down_front:   { angles: [0, -60, 85, 25],     t: 0.8 },
    up_front:     { angles: [0, -10, 20, -10],    t: 0.8 },
    reach_back:   { angles: [180, -30, 50, 0],    t: 1.2 },
    mid_right:    { angles: [40, -35, 45, -10],   t: 0.8 },
    mid_left:     { angles: [-40, -35, 45, -10],  t: 0.8 },
    mid_front:    { angles: [0, -40, 50, -5],     t: 0.8 },
  };

  static NAMED_PATTERNS = {
    pick_place: { poses: ["home", "reach_right:open", "down_right:grab", "up_right", "up_left", "down_left:release", "up_left", "home"], speed: 0.5 },
    welding:    { poses: ["ready", "mid_right", "reach_right", "mid_front", "reach_left", "mid_left", "ready"], speed: 0.35 },
    palletize:  { poses: ["home", "down_front:grab", "up_front", "reach_right", "down_right:release", "up_right", "home", "down_front:grab", "up_front", "reach_left", "down_left:release", "up_left", "home"], speed: 0.5 },
    inspect:    { poses: ["home", "reach_front", "reach_right", "reach_front", "reach_left", "reach_front", "home"], speed: 0.4 },
  };

  static _resolvePose(pose, joints, userPoses) {
    const DEG = Math.PI / 180;
    if (typeof pose === 'string') {
      let name = pose, grip;
      if (name.endsWith(':grab') || name.endsWith(':close')) { name = name.split(':')[0]; grip = 1; }
      else if (name.endsWith(':release') || name.endsWith(':open')) { name = name.split(':')[0]; grip = 0; }
      const np = (userPoses && userPoses[name]) || this.NAMED_POSES[name];
      if (!np) return { angles: joints.map(() => 0), duration: 1.0, grip };
      const src = np.angles || np;
      const a = (Array.isArray(src) ? src : []).slice(0, joints.length);
      while (a.length < joints.length) a.push(0);
      return { angles: a.map(d => d * DEG), duration: np.t || 1.0, grip };
    }
    if (Array.isArray(pose)) {
      const a = pose.slice(0, joints.length);
      while (a.length < joints.length) a.push(0);
      const grip = pose.length > joints.length ? pose[joints.length] : undefined;
      return { angles: a.map(d => d * DEG), duration: 1.0, grip };
    }
    const angles = joints.map(j => (pose[j.name] || 0) * DEG);
    return { angles, duration: pose.t || 1.0, grip: pose.grip };
  }

  // ════════════════════════════════════════════════════════════
  // create() — Lib3D 표준 인터페이스
  // ════════════════════════════════════════════════════════════

  static create(THREE, options = {}) {
    const spec = options.spec;
    if (!spec) return this._createFallback(THREE, options);

    const targetHeight = options.targetHeight || spec.height || 2.0;
    const gm = this._getGradientMap(THREE);
    const device = new THREE.Group();
    device.userData.deviceType = options._registeredName || spec.name || 'GenericDevice';

    // Internal state for animation
    const _state = { partMap: {}, animEntries: [], userPoses: {}, time: 0 };
    device.userData.__genericState = _state;

    // Load user-defined poses
    if (spec.poses) {
      for (const [name, val] of Object.entries(spec.poses)) {
        _state.userPoses[name] = Array.isArray(val) ? { angles: val, t: 1.0 } : val;
      }
    }

    // Build geometry
    if (spec.chain) {
      this._buildChain(THREE, spec.chain, spec.tool, device, _state);
    } else if (spec.parts) {
      this._processParts(THREE, spec.parts, device, _state, false);
    }

    // State indicator
    const box = new THREE.Box3().setFromObject(device);
    const sz = new THREE.Vector3(); box.getSize(sz);
    const cn = new THREE.Vector3(); box.getCenter(cn);
    const indMat = new THREE.MeshToonMaterial({
      color: 0x22d3ee, transparent: true, opacity: 0.2,
      emissive: new THREE.Color(0x22d3ee), emissiveIntensity: 0.4, gradientMap: gm
    });
    const indicator = new THREE.Mesh(new THREE.BoxGeometry(sz.x * 1.15, sz.y * 1.05, sz.z * 1.15), indMat);
    indicator.position.copy(cn);
    indicator.userData.isStateIndicator = true;
    device.add(indicator);

    // Scale to target height
    const curH = sz.y || 1;
    const s = targetHeight / curH;
    device.scale.set(s, s, s);

    // Prepare animation
    this._setupAnimations(spec, _state);

    return device;
  }

  static _createFallback(THREE, options) {
    // Fallback: Dummy-like box
    const gm = this._getGradientMap(THREE);
    const g = new THREE.Group();
    g.userData.deviceType = 'GenericDevice';
    const mesh = new THREE.Mesh(
      new THREE.BoxGeometry(1, 1.5, 1),
      new THREE.MeshToonMaterial({ color: 0x94a3b8, transparent: true, opacity: 0.8, gradientMap: gm })
    );
    mesh.position.y = 0.75;
    mesh.castShadow = true;
    g.add(mesh);
    const ind = new THREE.Mesh(
      new THREE.BoxGeometry(1.2, 1.7, 1.2),
      new THREE.MeshToonMaterial({ color: 0x22d3ee, transparent: true, opacity: 0.2, emissive: new THREE.Color(0x22d3ee), emissiveIntensity: 0.4, gradientMap: gm })
    );
    ind.position.y = 0.75;
    ind.userData.isStateIndicator = true;
    g.add(ind);
    const h = options.targetHeight || 2.0;
    const s = h / 1.5;
    g.scale.set(s, s, s);
    return g;
  }

  // ════════════════════════════════════════════════════════════
  // Parts Builder
  // ════════════════════════════════════════════════════════════

  static _processParts(THREE, parts, parent, st, isChild) {
    for (const part of parts) this._processPart(THREE, part, parent, st, isChild);
  }

  static _processPart(THREE, part, parent, st, isChild) {
    const geo = this._createGeometry(THREE, part);
    const mat = this._createMaterial(THREE, part);
    const mesh = new THREE.Mesh(geo, mat);
    mesh.castShadow = true;
    mesh.receiveShadow = true;

    const hasChildren = part.children && part.children.length > 0;

    // Position
    this._positionMesh(mesh, part, st, isChild);

    // Repeat
    if (part.repeat) {
      const clones = this._applyRepeat(mesh, part);
      const regList = [];
      for (const cl of clones) {
        if (hasChildren) {
          const g = new THREE.Group();
          g.position.copy(cl.position); cl.position.set(0, 0, 0);
          g.add(cl); this._addOutline(THREE, cl, g);
          this._processParts(THREE, part.children, g, st, true);
          parent.add(g); regList.push(g);
        } else {
          parent.add(cl); this._addOutline(THREE, cl, parent); regList.push(cl);
        }
      }
      if (part.id) st.partMap[part.id] = regList;
      return;
    }

    if (hasChildren) {
      const container = new THREE.Group();
      container.position.copy(mesh.position); mesh.position.set(0, 0, 0);
      container.add(mesh); this._addOutline(THREE, mesh, container);
      this._processParts(THREE, part.children, container, st, true);
      parent.add(container);
      if (part.id) {
        st.partMap[part.id] = [container];
        st.partMap['__info_' + part.id] = {
          topY: container.position.y + this._getHalfH(part),
          cy: container.position.y, cx: container.position.x, cz: container.position.z
        };
      }
    } else {
      parent.add(mesh); this._addOutline(THREE, mesh, parent);
      if (part.id) {
        st.partMap[part.id] = [mesh];
        st.partMap['__info_' + part.id] = {
          topY: mesh.position.y + this._getHalfH(part),
          cy: mesh.position.y, cx: mesh.position.x, cz: mesh.position.z
        };
      }
    }
  }

  static _positionMesh(mesh, part, st, isChild) {
    const hh = this._getHalfH(part);
    if (part.pos) { mesh.position.set(part.pos[0], part.pos[1], part.pos[2]); }
    else if (part.on) {
      const info = st.partMap['__info_' + part.on];
      if (info) mesh.position.set(info.cx, info.topY + hh, info.cz);
      else mesh.position.y = hh;
    } else if (!isChild) { mesh.position.y = hh; }
    if (part.offset) {
      mesh.position.x += part.offset[0];
      mesh.position.y += part.offset[1];
      mesh.position.z += part.offset[2];
    }
  }

  static _applyRepeat(mesh, part) {
    const rep = part.repeat;
    const offsets = [];
    if (rep.pattern === 'corners') {
      const w = (rep.size ? rep.size[0] : 1) / 2, d = (rep.size ? rep.size[1] : 1) / 2;
      offsets.push([-w,0,-d], [-w,0,d], [w,0,-d], [w,0,d]);
    } else if (rep.pattern === 'line') {
      const n = rep.count || 4, len = rep.length || 2, ax = rep.axis || 'x';
      for (let i = 0; i < n; i++) {
        const t = n > 1 ? (i / (n - 1) - 0.5) * len : 0;
        const o = [0, 0, 0];
        if (ax === 'x') o[0] = t; else if (ax === 'y') o[1] = t; else o[2] = t;
        offsets.push(o);
      }
    } else if (rep.pattern === 'circle') {
      const n = rep.count || 8, r = rep.radius || 1;
      for (let i = 0; i < n; i++) {
        const a = (i / n) * Math.PI * 2;
        offsets.push([Math.cos(a) * r, 0, Math.sin(a) * r]);
      }
    }
    if (offsets.length === 0) { for (let i = 0; i < (rep.count || 1); i++) offsets.push([i * 0.5, 0, 0]); }

    return offsets.map(off => {
      const cl = mesh.clone(); cl.material = mesh.material.clone();
      cl.position.set(mesh.position.x + off[0], mesh.position.y + off[1], mesh.position.z + off[2]);
      if (rep.pattern === 'circle') cl.rotation.y = -Math.atan2(off[2], off[0]) + Math.PI / 2;
      if (part.shape === 'cylinder' && rep.pattern === 'line') cl.rotation.z = Math.PI / 2;
      return cl;
    });
  }

  // ════════════════════════════════════════════════════════════
  // Chain Builder (Robot Arms)
  // ════════════════════════════════════════════════════════════

  static _buildChain(THREE, chain, tool, device, st) {
    const gm = this._getGradientMap(THREE);
    let currentGroup = device;
    const joints = [];
    const colors = [0xfbbf24, 0xfde047, 0xfbbf24, 0xfde047, 0xfbbf24];

    // Base platform
    const baseW = chain[0] ? chain[0].width : 0.9;
    const platH = 0.2;
    const platGeo = new THREE.CylinderGeometry(baseW * 0.65, baseW * 0.7, platH, 28);
    const platMesh = new THREE.Mesh(platGeo, new THREE.MeshToonMaterial({ color: 0x64748b, gradientMap: gm }));
    platMesh.position.y = platH / 2; platMesh.castShadow = true;
    device.add(platMesh); this._addOutline(THREE, platMesh, device);

    for (let i = 0; i < chain.length; i++) {
      const seg = chain[i], isBase = (i === 0);
      const w = seg.width || 0.3, len = seg.length || 0.5;

      const jointGroup = new THREE.Group();
      jointGroup.position.y = isBase ? platH : (chain[i - 1].length || 0.5);
      jointGroup.name = seg.name;
      currentGroup.add(jointGroup);
      joints.push({ group: jointGroup, axis: seg.axis || 'y', name: seg.name });

      if (isBase) {
        const hGeo = new THREE.CylinderGeometry(w * 0.4, w * 0.5, len, 22);
        const hMesh = new THREE.Mesh(hGeo, new THREE.MeshToonMaterial({ color: colors[0], gradientMap: gm, emissive: new THREE.Color(colors[0]), emissiveIntensity: 0.2 }));
        hMesh.position.y = len / 2; hMesh.castShadow = true;
        jointGroup.add(hMesh); this._addOutline(THREE, hMesh, jointGroup);
        const rGeo = new THREE.CylinderGeometry(w * 0.42, w * 0.42, 0.06, 22);
        const ring = new THREE.Mesh(rGeo, new THREE.MeshToonMaterial({ color: 0x475569, gradientMap: gm }));
        ring.position.y = len - 0.03; ring.castShadow = true; jointGroup.add(ring);
      } else {
        const jR = Math.min(w * 0.55, 0.18);
        const jMesh = new THREE.Mesh(new THREE.SphereGeometry(jR, 14, 14), new THREE.MeshToonMaterial({ color: 0xf59e0b, gradientMap: gm, emissive: new THREE.Color(0xf59e0b), emissiveIntensity: 0.3 }));
        jMesh.castShadow = true; jointGroup.add(jMesh); this._addOutline(THREE, jMesh, jointGroup);
        const col = colors[i % colors.length];
        const aGeo = new THREE.BoxGeometry(w, len - jR * 0.5, w * 0.75);
        const aMesh = new THREE.Mesh(aGeo, new THREE.MeshToonMaterial({ color: col, gradientMap: gm, emissive: new THREE.Color(col), emissiveIntensity: 0.15 }));
        aMesh.position.y = (len - jR * 0.5) / 2 + jR * 0.25; aMesh.castShadow = true;
        jointGroup.add(aMesh); this._addOutline(THREE, aMesh, jointGroup);
      }
      currentGroup = jointGroup;
    }

    // End effector
    const lastLen = chain[chain.length - 1].length || 0.5;
    const lastW = chain[chain.length - 1].width || 0.2;
    const flangeGeo = new THREE.CylinderGeometry(lastW * 0.45, lastW * 0.45, 0.08, 16);
    const flange = new THREE.Mesh(flangeGeo, new THREE.MeshToonMaterial({ color: 0x475569, gradientMap: gm }));
    flange.position.y = lastLen + 0.04; flange.castShadow = true; currentGroup.add(flange);

    const toolY = lastLen + 0.12;
    const fingerOpenX = lastW * 0.5, fingerCloseX = lastW * 0.12;
    if (tool === 'gripper') {
      const gb = new THREE.Mesh(new THREE.BoxGeometry(lastW * 1.2, 0.06, lastW * 0.8), new THREE.MeshToonMaterial({ color: 0x475569, gradientMap: gm }));
      gb.position.y = toolY; gb.castShadow = true; currentGroup.add(gb);
      const fGeo = new THREE.BoxGeometry(0.04, 0.16, lastW * 0.6);
      const fMat = new THREE.MeshToonMaterial({ color: 0x60a5fa, gradientMap: gm, emissive: new THREE.Color(0x3b82f6), emissiveIntensity: 0.4 });
      const fingers = [];
      [-fingerOpenX, fingerOpenX].forEach(x => {
        const f = new THREE.Mesh(fGeo, fMat); f.position.set(x, toolY + 0.11, 0); f.castShadow = true;
        currentGroup.add(f); this._addOutline(THREE, f, currentGroup); fingers.push(f);
      });
      st.partMap['__fingers'] = fingers;
      st.partMap['__finger_open'] = fingerOpenX;
      st.partMap['__finger_close'] = fingerCloseX;
    } else if (tool === 'vacuum') {
      const v = new THREE.Mesh(new THREE.ConeGeometry(0.1, 0.2, 16), new THREE.MeshToonMaterial({ color: 0x22d3ee, gradientMap: gm, emissive: new THREE.Color(0x06b6d4), emissiveIntensity: 0.5 }));
      v.position.y = toolY + 0.1; v.rotation.x = Math.PI; v.castShadow = true; currentGroup.add(v); this._addOutline(THREE, v, currentGroup);
    } else if (tool === 'welder') {
      const w2 = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.02, 0.22, 12), new THREE.MeshToonMaterial({ color: 0xef4444, gradientMap: gm, emissive: new THREE.Color(0xef4444), emissiveIntensity: 0.6 }));
      w2.position.y = toolY + 0.11; w2.castShadow = true; currentGroup.add(w2); this._addOutline(THREE, w2, currentGroup);
    }

    st.partMap['__chain_joints'] = joints;
  }

  // ════════════════════════════════════════════════════════════
  // Animation Setup
  //
  // 두 가지 모드:
  //   A) "animation.active" — 단일 애니메이션 (direction 무관, Going이면 실행)
  //   B) "dirs" — ApiDef별 독립 애니메이션 (direction 이름으로 분기)
  //
  // st.dirAnimMap: { "ADV": [...entries], "RET": [...entries] }  (모드 B)
  // st.animEntries: [...entries]  (모드 A, 또는 현재 활성 direction의 entries)
  // ════════════════════════════════════════════════════════════

  static _setupAnimations(spec, st) {
    st.animEntries = [];
    st.dirAnimMap = null; // null이면 단일 모드(A)

    // ── 모드 B: dirs 필드가 있으면 direction별 분기 ──
    if (spec.dirs && typeof spec.dirs === 'object') {
      st.dirAnimMap = {};
      for (const [dirName, animDef] of Object.entries(spec.dirs)) {
        st.dirAnimMap[dirName] = this._buildAnimEntries(animDef, st);
      }
      return;
    }

    // ── 모드 A: animation.active (기존 호환) ──
    const animSpec = spec.animation;
    if (!animSpec || !animSpec.active) return;
    st.animEntries = this._buildAnimEntries(animSpec.active, st);
  }

  /**
   * 하나의 애니메이션 정의를 엔트리 배열로 변환 (모드 A, B 공용)
   */
  static _buildAnimEntries(activeDef, st) {
    const entries = [];
    const joints = st.partMap['__chain_joints'];

    // Chain sequence
    if (joints && typeof activeDef === 'object' && activeDef.sequence) {
      const kf = activeDef.sequence.map(p => this._resolvePose(p, joints, st.userPoses));
      entries.push({ type: 'seq', joints, keyframes: kf, speed: activeDef.speed || 0.5, cf: 0, progress: 0, currentGrip: 0 });
      return entries;
    }

    // Named patterns / work
    if (joints && typeof activeDef === 'string') {
      const pattern = this.NAMED_PATTERNS[activeDef];
      if (pattern) {
        const kf = pattern.poses.map(p => this._resolvePose(p, joints, st.userPoses));
        entries.push({ type: 'seq', joints, keyframes: kf, speed: pattern.speed, cf: 0, progress: 0, currentGrip: 0 });
        return entries;
      }
      if (activeDef === 'work') {
        joints.forEach((j, i) => {
          const amp = j.axis === 'y' ? 0.4 : 0.35 + i * 0.12;
          entries.push({ type: 'swing', mesh: j.group, axis: j.axis, angle: amp, speed: 0.6 + i * 0.35, phase: i * 1.1 });
        });
        return entries;
      }
    }

    // Per-joint object (chain)
    if (joints && typeof activeDef === 'object' && !Array.isArray(activeDef) && !activeDef.target && !activeDef.sequence) {
      const cfg = activeDef;
      joints.forEach((j, i) => {
        const c = cfg[j.name];
        const amp = c ? (c.amplitude || 0.5) : 0.15;
        const spd = c ? (c.speed || 1.0) : 0.5;
        const ph = c ? (c.phase || i * 1.0) : i * 0.8;
        entries.push({ type: 'swing', mesh: j.group, axis: j.axis, angle: amp, speed: spd, phase: ph });
      });
      return entries;
    }

    // Standard part animations (move, spin, swing, roll, flow)
    const list = Array.isArray(activeDef) ? activeDef : [activeDef];
    for (const anim of list) {
      if (typeof anim === 'string') continue;
      const targets = st.partMap[anim.target];
      if (!targets || targets.length === 0) continue;
      entries.push({
        type: anim.type,
        meshes: targets,
        axis: anim.axis || 'y',
        speed: anim.speed || 1,
        min: anim.min, max: anim.max,
        angle: anim.angle || 0.5,
        phase: anim.phase || 0,
        restPositions: targets.map(m => m.position.clone()),
        restRotations: targets.map(m => m.rotation.clone())
      });
    }
    return entries;
  }

  // ════════════════════════════════════════════════════════════
  // updateState() — Lib3D 표준 인터페이스
  // ════════════════════════════════════════════════════════════

  static updateState(model, state) {
    const colors = { R: 0x22d3ee, G: 0xfde047, F: 0x60a5fa, H: 0xa78bfa };
    const color = colors[state] || 0x22d3ee;
    model.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  // ════════════════════════════════════════════════════════════
  // animate() — Lib3D 표준 인터페이스
  //   direction 파라미터: 시뮬레이션에서 활성 ApiDef 이름 전달
  //   GenericDevice에서는 "호출되면 시퀀스 1프레임 진행"으로 사용
  // ════════════════════════════════════════════════════════════

  static animate(model, direction, speed) {
    const st = model.userData.__genericState;
    if (!st) return false;

    st.time += 0.016;
    const isActive = !!direction; // direction이 있으면 Going

    // ── 모드 B: direction별 분기 ──
    // dirAnimMap이 있으면 direction에 해당하는 엔트리만 실행하고,
    // 나머지 direction의 엔트리는 rest로 복귀
    let activeEntries;
    if (st.dirAnimMap) {
      activeEntries = isActive && direction ? (st.dirAnimMap[direction] || []) : [];
      // 비활성 direction의 엔트리들은 rest 복귀
      for (const [dir, dirEntries] of Object.entries(st.dirAnimMap)) {
        if (dir === direction) continue;
        for (const entry of dirEntries) {
          this._returnToRest(entry, st);
        }
      }
    } else {
      activeEntries = st.animEntries;
    }

    for (const entry of activeEntries) {

      // ── Chain Sequence ──
      if (entry.type === 'seq') {
        const { joints, keyframes } = entry;
        const fingers = st.partMap['__fingers'];
        const openX = st.partMap['__finger_open'] || 0.07;
        const closeX = st.partMap['__finger_close'] || 0.02;

        if (!isActive) {
          // Return to rest (home)
          joints.forEach(j => {
            j.group.rotation.x *= 0.94; j.group.rotation.y *= 0.94; j.group.rotation.z *= 0.94;
          });
          if (fingers) {
            entry.currentGrip = (entry.currentGrip || 0) * 0.94;
            const d = openX - (openX - closeX) * entry.currentGrip;
            fingers[0].position.x = -d; fingers[1].position.x = d;
          }
          entry.cf = 0; entry.progress = 0;
          continue;
        }

        const dur = keyframes[entry.cf].duration || 1.0;
        entry.progress += 0.016 * (entry.speed || 0.5) / dur;
        if (entry.progress >= 1.0) { entry.progress = 0; entry.cf = (entry.cf + 1) % keyframes.length; }

        const t = entry.progress, st2 = t * t * (3 - 2 * t);
        const from = keyframes[entry.cf], to = keyframes[(entry.cf + 1) % keyframes.length];

        joints.forEach((j, i) => {
          const a0 = from.angles[i] || 0, a1 = to.angles[i] || 0;
          j.group.rotation[j.axis] = a0 + (a1 - a0) * st2;
        });

        if (fingers && fingers.length >= 2) {
          const g0 = from.grip !== undefined ? from.grip : (entry.currentGrip || 0);
          const g1 = to.grip !== undefined ? to.grip : g0;
          entry.currentGrip = g0 + (g1 - g0) * st2;
          const d = openX - (openX - closeX) * entry.currentGrip;
          fingers[0].position.x = -d; fingers[1].position.x = d;
        }
        continue;
      }

      // ── Swing ──
      if (entry.type === 'swing') {
        if (!isActive) { entry.mesh.rotation[entry.axis] *= 0.94; continue; }
        entry.mesh.rotation[entry.axis] = Math.sin(st.time * entry.speed + (entry.phase || 0)) * (entry.angle || 0.5);
        continue;
      }

      // ── Standard part animations ──
      const { meshes, axis, min, max, angle, phase, restPositions, restRotations } = entry;
      const axIdx = axis;
      for (let i = 0; i < meshes.length; i++) {
        const m = meshes[i];
        if (!isActive) {
          if (restPositions && restPositions[i]) {
            m.position.x += (restPositions[i].x - m.position.x) * 0.05;
            m.position.y += (restPositions[i].y - m.position.y) * 0.05;
            m.position.z += (restPositions[i].z - m.position.z) * 0.05;
          }
          if (restRotations && restRotations[i] && entry.type !== 'spin') {
            m.rotation.x += (restRotations[i].x - m.rotation.x) * 0.05;
            m.rotation.y += (restRotations[i].y - m.rotation.y) * 0.05;
            m.rotation.z += (restRotations[i].z - m.rotation.z) * 0.05;
          }
          continue;
        }
        if (entry.type === 'move' && min !== undefined && max !== undefined) {
          const t = (Math.sin(st.time * (entry.speed || 1) * 2) + 1) / 2;
          m.position[axIdx] = min + t * (max - min);
        } else if (entry.type === 'spin') {
          m.rotation[axIdx] += (entry.speed || 0.02);
        } else if (entry.type === 'roll') {
          m.rotation.x += (entry.speed || 0.05);
        }
      }
    }
    return false;
  }

  /**
   * 비활성 direction의 애니메이션 엔트리를 rest 위치로 복귀
   */
  static _returnToRest(entry, st) {
    if (entry.type === 'seq') {
      const { joints } = entry;
      if (joints) joints.forEach(j => {
        j.group.rotation.x *= 0.94; j.group.rotation.y *= 0.94; j.group.rotation.z *= 0.94;
      });
      const fingers = st.partMap['__fingers'];
      if (fingers) {
        entry.currentGrip = (entry.currentGrip || 0) * 0.94;
        const openX = st.partMap['__finger_open'] || 0.07;
        const closeX = st.partMap['__finger_close'] || 0.02;
        const d = openX - (openX - closeX) * entry.currentGrip;
        fingers[0].position.x = -d; fingers[1].position.x = d;
      }
      entry.cf = 0; entry.progress = 0;
    } else if (entry.type === 'swing' && entry.mesh) {
      entry.mesh.rotation[entry.axis] *= 0.94;
    } else if (entry.meshes) {
      const { meshes, restPositions, restRotations } = entry;
      for (let i = 0; i < meshes.length; i++) {
        const m = meshes[i];
        if (restPositions && restPositions[i]) {
          m.position.x += (restPositions[i].x - m.position.x) * 0.05;
          m.position.y += (restPositions[i].y - m.position.y) * 0.05;
          m.position.z += (restPositions[i].z - m.position.z) * 0.05;
        }
        if (restRotations && restRotations[i] && entry.type !== 'spin') {
          m.rotation.x += (restRotations[i].x - m.rotation.x) * 0.05;
          m.rotation.y += (restRotations[i].y - m.rotation.y) * 0.05;
          m.rotation.z += (restRotations[i].z - m.rotation.z) * 0.05;
        }
      }
    }
  }
}

if (typeof module !== 'undefined' && module.exports) { module.exports = GenericDevice; }
if (typeof window !== 'undefined') { window.GenericDevice = GenericDevice; }
