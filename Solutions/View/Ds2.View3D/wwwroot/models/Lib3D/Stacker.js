/**
 * Stacker - 적재기 (Cartoon Style)
 * 명령어: UP, DOWN, EXTEND
 * 용도: 수직 적재, 창고 보관
 * @version 1.0 - Cartoon/Toon rendering with vertical movement
 */

class Stacker {
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
      baseColor = 0xef4444,  // Red
      targetHeight = 3.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const stacker = new THREE.Group();
    stacker.userData.deviceType = 'Stacker';

    // ===== BASE =====
    const baseGeometry = new THREE.BoxGeometry(1.2, 0.3, 1.2);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x475569,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.15;
    base.castShadow = true;
    stacker.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.12);
    baseOutline.position.y = 0.15;
    stacker.add(baseOutline);

    // ===== VERTICAL RAILS (2개) =====
    const railGeometry = new THREE.BoxGeometry(0.12, 2.5, 0.12);
    const railMaterial = new THREE.MeshToonMaterial({
      color: baseColor,
      gradientMap: gradientMap,
      emissive: baseColor,
      emissiveIntensity: 0.2
    });

    [-0.4, 0.4].forEach(x => {
      const rail = new THREE.Mesh(railGeometry.clone(), railMaterial);
      rail.position.set(x, 1.55, 0);
      rail.castShadow = true;
      stacker.add(rail);
      const railOutline = this.createOutline(railGeometry.clone(), 0x000000, 1.15);
      railOutline.position.set(x, 1.55, 0);
      stacker.add(railOutline);
    });

    // ===== CARRIAGE (이동 캐리지) =====
    const carriageGroup = new THREE.Group();
    carriageGroup.position.y = 0.6;
    carriageGroup.userData.isCarriage = true;
    stacker.add(carriageGroup);

    const carriageGeometry = new THREE.BoxGeometry(0.9, 0.25, 0.6);
    const carriage = new THREE.Mesh(
      carriageGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.3
      })
    );
    carriage.castShadow = true;
    carriageGroup.add(carriage);
    const carriageOutline = this.createOutline(carriageGeometry.clone(), 0x000000, 1.15);
    carriageGroup.add(carriageOutline);

    // ===== FORKS (포크) =====
    const forkGroup = new THREE.Group();
    forkGroup.position.set(0.45, 0, 0);
    forkGroup.userData.isFork = true;
    carriageGroup.add(forkGroup);

    const forkGeometry = new THREE.BoxGeometry(0.8, 0.08, 0.15);
    [-0.15, 0.15].forEach(z => {
      const fork = new THREE.Mesh(
        forkGeometry.clone(),
        new THREE.MeshToonMaterial({
          color: 0x94a3b8,
          gradientMap: gradientMap
        })
      );
      fork.position.set(0, 0, z);
      fork.castShadow = true;
      forkGroup.add(fork);
      const forkOutline = this.createOutline(forkGeometry.clone(), 0x000000, 1.15);
      forkOutline.position.set(0, 0, z);
      forkGroup.add(forkOutline);
    });

    // ===== HYDRAULIC CYLINDER =====
    const cylinderGeometry = new THREE.CylinderGeometry(0.08, 0.08, 0.5, 12);
    const cylinder = new THREE.Mesh(
      cylinderGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    cylinder.position.set(-0.3, 0, 0);
    cylinder.castShadow = true;
    carriageGroup.add(cylinder);
    const cylinderOutline = this.createOutline(cylinderGeometry.clone(), 0x000000, 1.15);
    cylinderOutline.position.set(-0.3, 0, 0);
    carriageGroup.add(cylinderOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#ef4444';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 40px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('STACKER', 128, 80);
    ctx.fillStyle = '#fbbf24';
    ctx.fillText('STACKER', 128, 80);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.7, 0.35),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.5, 0.61);
    stacker.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(1.4, 3.0, 1.4),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 1.5, 0);
    indicator.userData.isStateIndicator = true;
    stacker.add(indicator);

    const bbox = new THREE.Box3().setFromObject(stacker);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    stacker.scale.set(scale, scale, scale);

    return stacker;
  }

  static updateState(stacker, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    stacker.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(stacker, direction, speed) {
    speed = speed || 0.05;
    const scale = stacker.scale.y;

    let carriage = null;
    let fork = null;

    stacker.traverse(child => {
      if (child.userData.isCarriage) carriage = child;
      if (child.userData.isFork) fork = child;
    });

    if (!carriage) return false;

    const minY = 0.6;
    const maxY = 2.5;
    const currentY = carriage.position.y / scale;

    if (direction === 'UP') {
      const targetY = maxY;
      const newY = currentY + (targetY - currentY) * speed;
      carriage.position.y = newY * scale;
      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'DOWN') {
      const targetY = minY;
      const newY = currentY + (targetY - currentY) * speed;
      carriage.position.y = newY * scale;
      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'EXTEND') {
      if (!fork) return false;
      if (!stacker.userData.forkExtension) stacker.userData.forkExtension = 0.45;
      if (!stacker.userData.forkDirection) stacker.userData.forkDirection = 1;

      stacker.userData.forkExtension += 0.01 * stacker.userData.forkDirection;

      if (stacker.userData.forkExtension >= 0.8) {
        stacker.userData.forkExtension = 0.8;
        stacker.userData.forkDirection = -1;
      } else if (stacker.userData.forkExtension <= 0.45) {
        stacker.userData.forkExtension = 0.45;
        stacker.userData.forkDirection = 1;
      }

      fork.position.x = stacker.userData.forkExtension;
      return false;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Stacker;
}

if (typeof window !== 'undefined') {
  window.Stacker = Stacker;
}
