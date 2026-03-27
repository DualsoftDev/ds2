/**
 * Jib_Crane - 지브 크레인 (Cartoon Style)
 * 명령어: ROTATE, EXTEND, RETRACT, HOIST
 * 용도: 회전 크레인, 국소 작업
 * @version 1.0 - Cartoon/Toon rendering with jib rotation and hoist
 */

class Jib_Crane {
  static createToonGradient(THREE) {
    const colors = new Uint8Array(3);
    colors[0] = 50;
    colors[1] = 180;
    colors[2] = 255;
    const gradientMap = new THREE.DataTexture(colors, colors.length, 1, THREE.LuminanceFormat);
    gradientMap.needsUpdate = true;
    return gradientMap;
  }

  static createOutline(geometry, color = 0x000000, thickness = 1.10) {
    const outlineMaterial = new THREE.MeshBasicMaterial({
      color: color,
      side: THREE.BackSide,
      depthWrite: false
    });
    const outline = new THREE.Mesh(geometry, outlineMaterial);
    outline.scale.multiplyScalar(thickness);
    outline.userData.isOutline = true;
    outline.renderOrder = -1;
    return outline;
  }

  static create(THREE, options = {}) {
    const {
      baseColor = 0x059669,  // Emerald
      targetHeight = 3.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const crane = new THREE.Group();
    crane.userData.deviceType = 'Jib_Crane';
    crane.userData.rotation = 0;

    // ===== BASE =====
    const baseGeometry = new THREE.CylinderGeometry(0.8, 1.0, 0.4, 24);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.2;
    base.castShadow = true;
    crane.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.12);
    baseOutline.position.y = 0.2;
    crane.add(baseOutline);

    // ===== VERTICAL COLUMN =====
    const columnGeometry = new THREE.CylinderGeometry(0.25, 0.3, 2.5, 20);
    const column = new THREE.Mesh(
      columnGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    column.position.y = 1.65;
    column.castShadow = true;
    crane.add(column);
    const columnOutline = this.createOutline(columnGeometry.clone(), 0x000000, 1.15);
    columnOutline.position.y = 1.65;
    crane.add(columnOutline);

    // ===== ROTATING JIB ARM =====
    const jibGroup = new THREE.Group();
    jibGroup.position.y = 2.9;
    jibGroup.userData.isJibArm = true;
    crane.add(jibGroup);

    // Pivot housing
    const pivotGeometry = new THREE.CylinderGeometry(0.35, 0.35, 0.4, 20);
    const pivot = new THREE.Mesh(
      pivotGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.4
      })
    );
    pivot.castShadow = true;
    jibGroup.add(pivot);
    const pivotOutline = this.createOutline(pivotGeometry.clone(), 0x000000, 1.15);
    jibGroup.add(pivotOutline);

    // Jib boom
    const boomGeometry = new THREE.BoxGeometry(3.0, 0.2, 0.2);
    const boom = new THREE.Mesh(
      boomGeometry,
      new THREE.MeshToonMaterial({
        color: 0xf97316,
        gradientMap: gradientMap,
        emissive: 0xea580c,
        emissiveIntensity: 0.3
      })
    );
    boom.position.set(1.5, 0.3, 0);
    boom.castShadow = true;
    jibGroup.add(boom);
    const boomOutline = this.createOutline(boomGeometry.clone(), 0x000000, 1.15);
    boomOutline.position.set(1.5, 0.3, 0);
    jibGroup.add(boomOutline);

    // Support strut
    const strutGeometry = new THREE.BoxGeometry(1.5, 0.12, 0.12);
    const strut = new THREE.Mesh(
      strutGeometry,
      new THREE.MeshToonMaterial({
        color: 0xf59e0b,
        gradientMap: gradientMap
      })
    );
    strut.position.set(0.75, 0.55, 0);
    strut.rotation.z = -Math.PI / 6;
    strut.castShadow = true;
    jibGroup.add(strut);

    // ===== TROLLEY =====
    const trolleyGroup = new THREE.Group();
    trolleyGroup.position.set(2.8, 0.3, 0);
    trolleyGroup.userData.isTrolley = true;
    jibGroup.add(trolleyGroup);

    const trolleyGeometry = new THREE.BoxGeometry(0.4, 0.3, 0.35);
    const trolley = new THREE.Mesh(
      trolleyGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.4
      })
    );
    trolley.castShadow = true;
    trolleyGroup.add(trolley);
    const trolleyOutline = this.createOutline(trolleyGeometry.clone(), 0x000000, 1.15);
    trolleyGroup.add(trolleyOutline);

    // ===== CABLE AND HOOK =====
    const hookGroup = new THREE.Group();
    hookGroup.position.y = -1.0;
    hookGroup.userData.isHook = true;
    trolleyGroup.add(hookGroup);

    // Cable
    const cableGeometry = new THREE.CylinderGeometry(0.04, 0.04, 1.5, 8);
    const cable = new THREE.Mesh(
      cableGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8,
        gradientMap: gradientMap
      })
    );
    cable.position.y = 0.75;
    cable.castShadow = true;
    hookGroup.add(cable);

    // Hook
    const hookGeometry = new THREE.CylinderGeometry(0.15, 0.12, 0.35, 12);
    const hook = new THREE.Mesh(
      hookGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.5
      })
    );
    hook.castShadow = true;
    hookGroup.add(hook);
    const hookOutline = this.createOutline(hookGeometry.clone(), 0x000000, 1.20);
    hookGroup.add(hookOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#059669';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 42px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('JIB', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('JIB', 128, 50);

    ctx.font = 'bold 36px Arial';
    ctx.strokeText('CRANE', 128, 95);
    ctx.fillStyle = '#059669';
    ctx.fillText('CRANE', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 2.0, 0);
    crane.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(4.0, 3.5, 1.5),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(1.5, 1.5, 0);
    indicator.userData.isStateIndicator = true;
    crane.add(indicator);

    const bbox = new THREE.Box3().setFromObject(crane);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    crane.scale.set(scale, scale, scale);

    return crane;
  }

  static updateState(crane, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    crane.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(crane, direction, speed) {
    speed = speed || 0.02;

    let jibArm = null;
    let trolley = null;
    let hook = null;

    crane.traverse(child => {
      if (child.userData.isJibArm) jibArm = child;
      if (child.userData.isTrolley) trolley = child;
      if (child.userData.isHook) hook = child;
    });

    if (direction === 'ROTATE') {
      // Rotate jib arm
      if (jibArm) {
        crane.userData.rotation += speed;
        jibArm.rotation.y = crane.userData.rotation;
      }
    } else if (direction === 'EXTEND') {
      // Extend trolley
      if (trolley) {
        trolley.position.x = Math.min(2.8, trolley.position.x + speed * 2);
      }
    } else if (direction === 'RETRACT') {
      // Retract trolley
      if (trolley) {
        trolley.position.x = Math.max(0.5, trolley.position.x - speed * 2);
      }
    } else if (direction === 'HOIST') {
      // Hoist up and down
      if (hook) {
        if (!hook.userData.hoistDirection) hook.userData.hoistDirection = 1;
        hook.position.y += speed * hook.userData.hoistDirection;

        if (hook.position.y >= -0.3) {
          hook.position.y = -0.3;
          hook.userData.hoistDirection = -1;
        } else if (hook.position.y <= -1.5) {
          hook.position.y = -1.5;
          hook.userData.hoistDirection = 1;
        }
      }
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Jib_Crane;
}

if (typeof window !== 'undefined') {
  window.Jib_Crane = Jib_Crane;
}
