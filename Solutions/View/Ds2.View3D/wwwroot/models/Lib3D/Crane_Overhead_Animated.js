/**
 * Crane_Overhead_Animated - 오버헤드 크레인 (Cartoon Style)
 * 명령어: MOVE_X, MOVE_Y, HOIST_UP, HOIST_DOWN
 * 용도: 중량물 이송, 크레인 작업
 * @version 1.0 - Cartoon/Toon rendering with XYZ crane motion
 */

class Crane_Overhead_Animated {
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
      baseColor = 0xeab308,  // Yellow
      targetHeight = 4.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const crane = new THREE.Group();
    crane.userData.deviceType = 'Crane_Overhead_Animated';
    crane.userData.position = { x: 0, y: 0, z: 0 };

    // ===== SUPPORT COLUMNS =====
    const columnGeometry = new THREE.BoxGeometry(0.3, 3.5, 0.3);
    const columnMaterial = new THREE.MeshToonMaterial({
      color: 0x64748b,
      gradientMap: gradientMap
    });

    const columnPositions = [
      [-3, 1.75, -2],
      [-3, 1.75, 2],
      [3, 1.75, -2],
      [3, 1.75, 2]
    ];

    columnPositions.forEach(pos => {
      const column = new THREE.Mesh(columnGeometry.clone(), columnMaterial);
      column.position.set(...pos);
      column.castShadow = true;
      crane.add(column);
      const columnOutline = this.createOutline(columnGeometry.clone(), 0x000000, 1.12);
      columnOutline.position.set(...pos);
      crane.add(columnOutline);
    });

    // ===== MAIN BRIDGE BEAM (X-axis) =====
    const bridgeGeometry = new THREE.BoxGeometry(6.5, 0.4, 0.5);
    const bridge = new THREE.Mesh(
      bridgeGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    bridge.position.set(0, 3.5, 0);
    bridge.castShadow = true;
    crane.add(bridge);
    const bridgeOutline = this.createOutline(bridgeGeometry.clone(), 0x000000, 1.12);
    bridgeOutline.position.set(0, 3.5, 0);
    crane.add(bridgeOutline);

    // ===== TROLLEY (Y-axis movement) =====
    const trolleyGroup = new THREE.Group();
    trolleyGroup.position.set(0, 3.5, 0);
    trolleyGroup.userData.isTrolley = true;
    crane.add(trolleyGroup);

    const trolleyGeometry = new THREE.BoxGeometry(0.8, 0.5, 0.6);
    const trolley = new THREE.Mesh(
      trolleyGeometry,
      new THREE.MeshToonMaterial({
        color: 0xf97316,
        gradientMap: gradientMap,
        emissive: 0xea580c,
        emissiveIntensity: 0.4
      })
    );
    trolley.castShadow = true;
    trolleyGroup.add(trolley);
    const trolleyOutline = this.createOutline(trolleyGeometry.clone(), 0x000000, 1.15);
    trolleyGroup.add(trolleyOutline);

    // Trolley wheels
    const wheelGeometry = new THREE.CylinderGeometry(0.08, 0.08, 0.12, 12);
    const wheelMaterial = new THREE.MeshToonMaterial({
      color: 0x1e293b,
      gradientMap: gradientMap
    });

    [-0.3, 0.3].forEach(x => {
      const wheel = new THREE.Mesh(wheelGeometry.clone(), wheelMaterial);
      wheel.position.set(x, 0.3, 0);
      wheel.rotation.z = Math.PI / 2;
      wheel.castShadow = true;
      trolleyGroup.add(wheel);
    });

    // ===== HOIST CABLE (Z-axis) =====
    const cableGroup = new THREE.Group();
    cableGroup.position.y = -1.5;
    cableGroup.userData.isHoist = true;
    trolleyGroup.add(cableGroup);

    const cableGeometry = new THREE.CylinderGeometry(0.04, 0.04, 2.0, 8);
    const cable = new THREE.Mesh(
      cableGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8,
        gradientMap: gradientMap
      })
    );
    cable.castShadow = true;
    cableGroup.add(cable);
    const cableOutline = this.createOutline(cableGeometry.clone(), 0x000000, 1.15);
    cableGroup.add(cableOutline);

    // ===== HOOK =====
    const hookGeometry = new THREE.CylinderGeometry(0.2, 0.15, 0.4, 12);
    const hook = new THREE.Mesh(
      hookGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.5
      })
    );
    hook.position.y = -1.2;
    hook.castShadow = true;
    cableGroup.add(hook);
    const hookOutline = this.createOutline(hookGeometry.clone(), 0x000000, 1.20);
    hookOutline.position.y = -1.2;
    cableGroup.add(hookOutline);

    // Hook tip
    const hookTipGeometry = new THREE.TorusGeometry(0.15, 0.05, 8, 12, Math.PI);
    const hookTip = new THREE.Mesh(
      hookTipGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap
      })
    );
    hookTip.position.y = -1.4;
    hookTip.rotation.x = Math.PI / 2;
    hookTip.castShadow = true;
    cableGroup.add(hookTip);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#eab308';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('OVERHEAD', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('OVERHEAD', 128, 50);

    ctx.strokeText('CRANE', 128, 95);
    ctx.fillStyle = '#eab308';
    ctx.fillText('CRANE', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(1.0, 0.5),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 2.5, 0);
    crane.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(7.0, 4.0, 4.5),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 2.0, 0);
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

    let trolley = null;
    let hoist = null;

    crane.traverse(child => {
      if (child.userData.isTrolley) trolley = child;
      if (child.userData.isHoist) hoist = child;
    });

    if (!crane.userData.position) {
      crane.userData.position = { x: 0, y: 0, z: 0 };
    }
    if (!crane.userData.direction) {
      crane.userData.direction = { x: 1, y: 1, z: 1 };
    }

    const pos = crane.userData.position;
    const dir = crane.userData.direction;

    if (direction === 'MOVE_X') {
      // Bridge movement (X-axis)
      if (trolley) {
        pos.x += speed * dir.x * 3;
        if (pos.x >= 2.5) {
          pos.x = 2.5;
          dir.x = -1;
        } else if (pos.x <= -2.5) {
          pos.x = -2.5;
          dir.x = 1;
        }
        trolley.position.x = pos.x;
      }
    } else if (direction === 'MOVE_Y') {
      // Trolley movement (Y-axis)
      if (trolley) {
        pos.y += speed * dir.y * 2;
        if (pos.y >= 1.5) {
          pos.y = 1.5;
          dir.y = -1;
        } else if (pos.y <= -1.5) {
          pos.y = -1.5;
          dir.y = 1;
        }
        trolley.position.z = pos.y;
      }
    } else if (direction === 'HOIST_UP') {
      // Hoist up
      if (hoist) {
        pos.z = Math.max(-0.5, pos.z - speed);
        hoist.position.y = -1.5 + pos.z;
      }
    } else if (direction === 'HOIST_DOWN') {
      // Hoist down
      if (hoist) {
        pos.z = Math.min(0.5, pos.z + speed);
        hoist.position.y = -1.5 + pos.z;
      }
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Crane_Overhead_Animated;
}

if (typeof window !== 'undefined') {
  window.Crane_Overhead_Animated = Crane_Overhead_Animated;
}
