/**
 * Pusher_Pneumatic - 공압 푸셔 (Cartoon Style)
 * 명령어: PUSH, RETRACT, HOLD
 * 용도: 제품 밀기, 방향 전환
 * @version 1.0 - Cartoon/Toon rendering with pneumatic pusher motion
 */

class Pusher_Pneumatic {
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
      baseColor = 0x8b5cf6,  // Violet
      targetHeight = 1.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const pusher = new THREE.Group();
    pusher.userData.deviceType = 'Pusher_Pneumatic';

    // ===== BASE MOUNT =====
    const baseGeometry = new THREE.BoxGeometry(0.5, 0.3, 0.6);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.55;
    base.castShadow = true;
    pusher.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.12);
    baseOutline.position.y = 0.55;
    pusher.add(baseOutline);

    // ===== CYLINDER BODY =====
    const cylinderGeometry = new THREE.CylinderGeometry(0.15, 0.15, 0.8, 16);
    const cylinder = new THREE.Mesh(
      cylinderGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    cylinder.position.set(0.4, 0.55, 0);
    cylinder.rotation.z = Math.PI / 2;
    cylinder.castShadow = true;
    pusher.add(cylinder);
    const cylinderOutline = this.createOutline(cylinderGeometry.clone(), 0x000000, 1.15);
    cylinderOutline.position.set(0.4, 0.55, 0);
    cylinderOutline.rotation.z = Math.PI / 2;
    pusher.add(cylinderOutline);

    // ===== PISTON ROD (moves in/out) =====
    const rodGroup = new THREE.Group();
    rodGroup.userData.isRod = true;
    pusher.add(rodGroup);

    const rodGeometry = new THREE.CylinderGeometry(0.08, 0.08, 0.6, 12);
    const rod = new THREE.Mesh(
      rodGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8,
        gradientMap: gradientMap
      })
    );
    rod.position.set(1.1, 0.55, 0);
    rod.rotation.z = Math.PI / 2;
    rod.castShadow = true;
    rodGroup.add(rod);
    const rodOutline = this.createOutline(rodGeometry.clone(), 0x000000, 1.15);
    rodOutline.position.set(1.1, 0.55, 0);
    rodOutline.rotation.z = Math.PI / 2;
    rodGroup.add(rodOutline);

    // ===== PUSHER PLATE =====
    const plateGeometry = new THREE.BoxGeometry(0.1, 0.5, 0.4);
    const plate = new THREE.Mesh(
      plateGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.4
      })
    );
    plate.position.set(1.45, 0.55, 0);
    plate.castShadow = true;
    rodGroup.add(plate);
    const plateOutline = this.createOutline(plateGeometry.clone(), 0x000000, 1.18);
    plateOutline.position.set(1.45, 0.55, 0);
    rodGroup.add(plateOutline);

    // Rubber pad
    const padGeometry = new THREE.BoxGeometry(0.12, 0.45, 0.35);
    const pad = new THREE.Mesh(
      padGeometry,
      new THREE.MeshToonMaterial({
        color: 0x1e293b,
        gradientMap: gradientMap
      })
    );
    pad.position.set(1.51, 0.55, 0);
    pad.castShadow = true;
    rodGroup.add(pad);

    // ===== AIR SUPPLY TUBE =====
    const tubeGeometry = new THREE.CylinderGeometry(0.04, 0.04, 0.4, 8);
    const tube = new THREE.Mesh(
      tubeGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap
      })
    );
    tube.position.set(0.1, 0.75, 0.2);
    tube.rotation.x = Math.PI / 4;
    tube.castShadow = true;
    pusher.add(tube);

    // ===== PRESSURE GAUGE =====
    const gaugeGeometry = new THREE.CylinderGeometry(0.08, 0.08, 0.08, 16);
    const gauge = new THREE.Mesh(
      gaugeGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.5
      })
    );
    gauge.position.set(0, 0.75, 0.25);
    gauge.rotation.x = Math.PI / 2;
    gauge.castShadow = true;
    pusher.add(gauge);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#8b5cf6';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('PNEUMATIC', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('PNEUMATIC', 128, 50);

    ctx.strokeText('PUSHER', 128, 95);
    ctx.fillStyle = '#8b5cf6';
    ctx.fillText('PUSHER', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.6, 0.3),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0.7, 0.95, 0);
    pusher.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(2.0, 1.0, 0.8),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0.75, 0.55, 0);
    indicator.userData.isStateIndicator = true;
    pusher.add(indicator);

    const bbox = new THREE.Box3().setFromObject(pusher);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    pusher.scale.set(scale, scale, scale);

    return pusher;
  }

  static updateState(pusher, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    pusher.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(pusher, direction, speed) {
    speed = speed || 0.05;

    let rod = null;

    pusher.traverse(child => {
      if (child.userData.isRod) rod = child;
    });

    if (!rod) return false;

    const minX = 0;
    const maxX = 0.8;
    const currentX = rod.position.x;

    if (direction === 'PUSH') {
      // Extend pusher
      const targetX = maxX;
      const newX = currentX + (targetX - currentX) * speed;
      rod.position.x = newX;
      return Math.abs(newX - targetX) < 0.01;
    } else if (direction === 'RETRACT') {
      // Retract pusher
      const targetX = minX;
      const newX = currentX + (targetX - currentX) * speed;
      rod.position.x = newX;
      return Math.abs(newX - targetX) < 0.01;
    } else if (direction === 'HOLD') {
      // Hold position
      return false;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Pusher_Pneumatic;
}

if (typeof window !== 'undefined') {
  window.Pusher_Pneumatic = Pusher_Pneumatic;
}
