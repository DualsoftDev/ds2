/**
 * Gripper_Vacuum - 진공 그리퍼 (Cartoon Style)
 * 명령어: SUCTION_ON, SUCTION_OFF, PICKUP
 * 용도: 매끄러운 표면 파지
 * @version 1.0 - Cartoon/Toon rendering with vacuum gripper animation
 */

class Gripper_Vacuum {
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
      baseColor = 0x06b6d4,  // Cyan
      targetHeight = 1.2
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const gripper = new THREE.Group();
    gripper.userData.deviceType = 'Gripper_Vacuum';
    gripper.userData.suctionActive = false;

    // ===== MOUNTING FLANGE =====
    const flangeGeometry = new THREE.CylinderGeometry(0.3, 0.3, 0.12, 20);
    const flange = new THREE.Mesh(
      flangeGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    flange.position.y = 0.55;
    flange.castShadow = true;
    gripper.add(flange);
    const flangeOutline = this.createOutline(flangeGeometry.clone(), 0x000000, 1.12);
    flangeOutline.position.y = 0.55;
    gripper.add(flangeOutline);

    // ===== VACUUM GENERATOR =====
    const generatorGeometry = new THREE.BoxGeometry(0.35, 0.3, 0.35);
    const generator = new THREE.Mesh(
      generatorGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    generator.position.y = 0.3;
    generator.castShadow = true;
    gripper.add(generator);
    const generatorOutline = this.createOutline(generatorGeometry.clone(), 0x000000, 1.15);
    generatorOutline.position.y = 0.3;
    gripper.add(generatorOutline);

    // ===== VACUUM CUPS (4개) =====
    const cupPositions = [
      [-0.15, -0.15],
      [-0.15, 0.15],
      [0.15, -0.15],
      [0.15, 0.15]
    ];

    cupPositions.forEach(([x, z]) => {
      const cupGroup = new THREE.Group();
      cupGroup.position.set(x, 0.05, z);
      cupGroup.userData.isVacuumCup = true;
      gripper.add(cupGroup);

      // Tube
      const tubeGeometry = new THREE.CylinderGeometry(0.04, 0.04, 0.2, 12);
      const tube = new THREE.Mesh(
        tubeGeometry,
        new THREE.MeshToonMaterial({
          color: 0x94a3b8,
          gradientMap: gradientMap
        })
      );
      tube.castShadow = true;
      cupGroup.add(tube);
      const tubeOutline = this.createOutline(tubeGeometry.clone(), 0x000000, 1.15);
      cupGroup.add(tubeOutline);

      // Cup
      const cupGeometry = new THREE.ConeGeometry(0.12, 0.18, 16);
      const cup = new THREE.Mesh(
        cupGeometry,
        new THREE.MeshToonMaterial({
          color: 0x60a5fa,
          gradientMap: gradientMap,
          transparent: true,
          opacity: 0.85,
          emissive: 0x3b82f6,
          emissiveIntensity: 0.2
        })
      );
      cup.position.y = -0.19;
      cup.castShadow = true;
      cupGroup.add(cup);
      const cupOutline = this.createOutline(cupGeometry.clone(), 0x000000, 1.15);
      cupOutline.position.y = -0.19;
      cupGroup.add(cupOutline);

      // Suction indicator (glowing when active)
      const indicatorGeometry = new THREE.RingGeometry(0.08, 0.12, 16);
      const indicator = new THREE.Mesh(
        indicatorGeometry,
        new THREE.MeshBasicMaterial({
          color: 0x22d3ee,
          transparent: true,
          opacity: 0,
          side: THREE.DoubleSide
        })
      );
      indicator.position.y = -0.28;
      indicator.rotation.x = Math.PI / 2;
      indicator.userData.isSuctionIndicator = true;
      cupGroup.add(indicator);
    });

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#06b6d4';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('VACUUM', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('VACUUM', 128, 50);

    ctx.strokeText('GRIPPER', 128, 95);
    ctx.fillStyle = '#06b6d4';
    ctx.fillText('GRIPPER', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.6, 0.3),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.7, 0.2);
    gripper.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(0.8, 1.0, 0.8),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 0.2, 0);
    indicator.userData.isStateIndicator = true;
    gripper.add(indicator);

    const bbox = new THREE.Box3().setFromObject(gripper);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    gripper.scale.set(scale, scale, scale);

    return gripper;
  }

  static updateState(gripper, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    gripper.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(gripper, direction, speed) {
    speed = speed || 0.05;

    if (direction === 'SUCTION_ON') {
      // Activate suction (glow effect)
      gripper.userData.suctionActive = true;
      gripper.traverse(child => {
        if (child.userData.isSuctionIndicator && child.material) {
          child.material.opacity = Math.min(1.0, child.material.opacity + speed);
        }
        if (child.userData.isVacuumCup) {
          child.scale.y = Math.max(0.9, child.scale.y - speed * 0.2);
        }
      });
      return false;
    } else if (direction === 'SUCTION_OFF') {
      // Deactivate suction
      gripper.userData.suctionActive = false;
      gripper.traverse(child => {
        if (child.userData.isSuctionIndicator && child.material) {
          child.material.opacity = Math.max(0, child.material.opacity - speed);
        }
        if (child.userData.isVacuumCup) {
          child.scale.y = Math.min(1.0, child.scale.y + speed * 0.2);
        }
      });
      return false;
    } else if (direction === 'PICKUP') {
      // Pulsing suction effect
      const time = Date.now() * 0.003;
      gripper.traverse(child => {
        if (child.userData.isSuctionIndicator && child.material) {
          child.material.opacity = 0.5 + Math.sin(time) * 0.5;
        }
      });
      return false;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Gripper_Vacuum;
}

if (typeof window !== 'undefined') {
  window.Gripper_Vacuum = Gripper_Vacuum;
}
