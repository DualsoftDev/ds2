/**
 * Lifter - 승강 리프터 (Cartoon Style)
 * 명령어: UP, DOWN
 * 용도: 수직 승강 동작
 * @version 2.0 - Cartoon/Toon rendering with texture mapping
 */

class Lifter {
  /**
   * Create toon gradient for strong cel-shading effect (cartoon style)
   */
  static createToonGradient(THREE) {
    // Only 2 levels for strong cartoon effect
    const colors = new Uint8Array(3);
    colors[0] = 50;   // Shadow
    colors[1] = 180;  // Mid-tone
    colors[2] = 255;  // Highlight

    const gradientMap = new THREE.DataTexture(colors, colors.length, 1, THREE.LuminanceFormat);
    gradientMap.needsUpdate = true;
    return gradientMap;
  }

  /**
   * Create thick outline mesh for strong cartoon effect
   */
  static createOutline(geometry, color = 0x000000, thickness = 1.10) {
    const outlineMaterial = new THREE.MeshBasicMaterial({
      color: color,
      side: THREE.BackSide
    });
    const outline = new THREE.Mesh(geometry, outlineMaterial);
    outline.scale.multiplyScalar(thickness);
    outline.userData.isOutline = true;
    return outline;
  }

  /**
   * Create bold striped texture for warning areas (cartoon style)
   */
  static createStripedTexture() {
    const canvas = document.createElement('canvas');
    canvas.width = 128;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    // Bright yellow and black bold diagonal stripes
    ctx.fillStyle = '#fde047'; // Brighter yellow
    ctx.fillRect(0, 0, 128, 128);

    ctx.strokeStyle = '#000000'; // Pure black
    ctx.lineWidth = 12; // Thicker stripes
    for (let i = -128; i < 256; i += 20) {
      ctx.beginPath();
      ctx.moveTo(i, 0);
      ctx.lineTo(i + 128, 128);
      ctx.stroke();
    }

    const texture = new THREE.CanvasTexture(canvas);
    texture.wrapS = THREE.RepeatWrapping;
    texture.wrapT = THREE.RepeatWrapping;
    return texture;
  }

  /**
   * Create bold metal panel texture (cartoon style)
   */
  static createMetalTexture(baseColor) {
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 256;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = baseColor;
    ctx.fillRect(0, 0, 256, 256);

    // Add bold panel lines
    ctx.strokeStyle = 'rgba(0, 0, 0, 0.6)'; // Stronger lines
    ctx.lineWidth = 4; // Thicker lines
    ctx.strokeRect(10, 10, 236, 236);

    // Add bigger, bolder rivets
    ctx.fillStyle = 'rgba(0, 0, 0, 0.7)';
    [20, 236].forEach(x => {
      [20, 236].forEach(y => {
        ctx.beginPath();
        ctx.arc(x, y, 6, 0, Math.PI * 2); // Bigger rivets
        ctx.fill();

        // Add white highlight for cartoon effect
        ctx.fillStyle = 'rgba(255, 255, 255, 0.5)';
        ctx.beginPath();
        ctx.arc(x - 2, y - 2, 2, 0, Math.PI * 2);
        ctx.fill();
        ctx.fillStyle = 'rgba(0, 0, 0, 0.7)';
      });
    });

    const texture = new THREE.CanvasTexture(canvas);
    return texture;
  }

  static create(THREE, options = {}) {
    const baseColor = options.baseColor || 0xfbbf24; // Brighter amber
    const targetHeight = options.targetHeight || 2.5;
    const gradientMap = this.createToonGradient(THREE);

    const lifter = new THREE.Group();
    lifter.userData.deviceType = 'Lifter';

    // Base platform with bright cartoon colors
    const baseGeometry = new THREE.BoxGeometry(1.2, 0.25, 1.2); // Slightly taller
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b, // Brighter slate gray
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.125;
    base.castShadow = true;
    lifter.add(base);

    // Add thick outline to base
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.15);
    baseOutline.position.y = 0.125;
    lifter.add(baseOutline);

    // Guide rails (4 corners) with bright toon material
    const metalTexture = this.createMetalTexture('#fbbf24');
    const railMaterial = new THREE.MeshToonMaterial({
      color: baseColor,
      gradientMap: gradientMap,
      map: metalTexture,
      emissive: baseColor,
      emissiveIntensity: 0.2
    });

    const railGeometry = new THREE.BoxGeometry(0.1, 2.5, 0.1); // Slightly thicker

    const rail1 = new THREE.Mesh(railGeometry.clone(), railMaterial);
    rail1.position.set(-0.5, 1.35, -0.5);
    rail1.castShadow = true;
    lifter.add(rail1);
    const rail1Outline = this.createOutline(railGeometry.clone(), 0x000000, 1.20);
    rail1Outline.position.set(-0.5, 1.35, -0.5);
    lifter.add(rail1Outline);

    const rail2 = new THREE.Mesh(railGeometry.clone(), railMaterial);
    rail2.position.set(0.5, 1.35, -0.5);
    rail2.castShadow = true;
    lifter.add(rail2);
    const rail2Outline = this.createOutline(railGeometry.clone(), 0x000000, 1.20);
    rail2Outline.position.set(0.5, 1.35, -0.5);
    lifter.add(rail2Outline);

    const rail3 = new THREE.Mesh(railGeometry.clone(), railMaterial);
    rail3.position.set(-0.5, 1.35, 0.5);
    rail3.castShadow = true;
    lifter.add(rail3);
    const rail3Outline = this.createOutline(railGeometry.clone(), 0x000000, 1.20);
    rail3Outline.position.set(-0.5, 1.35, 0.5);
    lifter.add(rail3Outline);

    const rail4 = new THREE.Mesh(railGeometry.clone(), railMaterial);
    rail4.position.set(0.5, 1.35, 0.5);
    rail4.castShadow = true;
    lifter.add(rail4);
    const rail4Outline = this.createOutline(railGeometry.clone(), 0x000000, 1.20);
    rail4Outline.position.set(0.5, 1.35, 0.5);
    lifter.add(rail4Outline);

    // Lift platform with bright toon material
    const platformGeometry = new THREE.BoxGeometry(1.0, 0.15, 1.0);
    const platform = new THREE.Mesh(
      platformGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8, // Brighter slate
        gradientMap: gradientMap,
        emissive: 0x475569,
        emissiveIntensity: 0.15
      })
    );
    platform.position.y = 0.5;
    platform.castShadow = true;
    platform.userData.isLiftPlatform = true;
    lifter.add(platform);
    const platformOutline = this.createOutline(platformGeometry.clone(), 0x000000, 1.18);
    platformOutline.position.y = 0.5;
    lifter.add(platformOutline);

    // Safety edges with bright striped warning texture
    const stripedTexture = this.createStripedTexture();
    const edgeMaterial = new THREE.MeshToonMaterial({
      color: 0xfde047, // Brighter yellow
      gradientMap: gradientMap,
      map: stripedTexture,
      emissive: 0xfbbf24,
      emissiveIntensity: 0.3
    });

    const edge1Geometry = new THREE.BoxGeometry(1.05, 0.08, 0.05);
    const edge1 = new THREE.Mesh(edge1Geometry, edgeMaterial);
    edge1.position.set(0, 0.5, 0.5);
    edge1.castShadow = true;
    lifter.add(edge1);
    const edge1Outline = this.createOutline(edge1Geometry.clone(), 0x000000, 1.25);
    edge1Outline.position.set(0, 0.5, 0.5);
    lifter.add(edge1Outline);

    const edge2Geometry = new THREE.BoxGeometry(1.05, 0.08, 0.05);
    const edge2 = new THREE.Mesh(edge2Geometry, edgeMaterial);
    edge2.position.set(0, 0.5, -0.5);
    edge2.castShadow = true;
    lifter.add(edge2);
    const edge2Outline = this.createOutline(edge2Geometry.clone(), 0x000000, 1.25);
    edge2Outline.position.set(0, 0.5, -0.5);
    lifter.add(edge2Outline);

    // Motor housing (top) with bright toon material
    const motorGeometry = new THREE.CylinderGeometry(0.18, 0.18, 0.3, 16);
    const motor = new THREE.Mesh(
      motorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x334155, // Lighter dark blue
        gradientMap: gradientMap,
        emissive: 0x1e293b,
        emissiveIntensity: 0.2
      })
    );
    motor.position.y = 2.73;
    motor.castShadow = true;
    lifter.add(motor);
    const motorOutline = this.createOutline(motorGeometry.clone(), 0x000000, 1.18);
    motorOutline.position.y = 2.73;
    lifter.add(motorOutline);

    // Cables (4개) with toon material and thicker for visibility
    const cableGeometry = new THREE.CylinderGeometry(0.02, 0.02, 2.3, 8);
    const cableMaterial = new THREE.MeshToonMaterial({
      color: 0x27272a, // Slightly lighter black
      gradientMap: gradientMap
    });

    const cable1 = new THREE.Mesh(cableGeometry.clone(), cableMaterial);
    cable1.position.set(0.42, 1.35, 0);
    lifter.add(cable1);

    const cable2 = new THREE.Mesh(cableGeometry.clone(), cableMaterial);
    cable2.position.set(-0.42, 1.35, 0);
    lifter.add(cable2);

    const cable3 = new THREE.Mesh(cableGeometry.clone(), cableMaterial);
    cable3.position.set(0, 1.35, 0.42);
    lifter.add(cable3);

    const cable4 = new THREE.Mesh(cableGeometry.clone(), cableMaterial);
    cable4.position.set(0, 1.35, -0.42);
    lifter.add(cable4);

    // Limit switches with bright glowing cartoon colors
    const switchDownGeometry = new THREE.BoxGeometry(0.1, 0.1, 0.1);
    const switchDown = new THREE.Mesh(
      switchDownGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee, // Bright cyan
        emissive: 0x22d3ee,
        emissiveIntensity: 0.8,
        gradientMap: gradientMap
      })
    );
    switchDown.position.set(-0.55, 0.4, 0);
    lifter.add(switchDown);
    const switchDownOutline = this.createOutline(switchDownGeometry.clone(), 0x000000, 1.25);
    switchDownOutline.position.set(-0.55, 0.4, 0);
    lifter.add(switchDownOutline);

    const switchUpGeometry = new THREE.BoxGeometry(0.1, 0.1, 0.1);
    const switchUp = new THREE.Mesh(
      switchUpGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfb7185, // Bright pink-red
        emissive: 0xfb7185,
        emissiveIntensity: 0.8,
        gradientMap: gradientMap
      })
    );
    switchUp.position.set(-0.55, 2.4, 0);
    lifter.add(switchUp);
    const switchUpOutline = this.createOutline(switchUpGeometry.clone(), 0x000000, 1.25);
    switchUpOutline.position.set(-0.55, 2.4, 0);
    lifter.add(switchUpOutline);

    // UP/DOWN label with bold cartoon/game style
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    // Bright background with thick border
    ctx.fillStyle = '#334155'; // Lighter background
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#fbbf24'; // Bright amber border
    ctx.lineWidth = 6; // Thicker border
    ctx.strokeRect(6, 6, 244, 116);

    // Text with thick outline (strong cartoon style)
    ctx.font = 'bold 44px Arial'; // Bigger text
    ctx.textAlign = 'center';

    // UP text with thick black outline
    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8; // Thicker outline
    ctx.strokeText('UP', 128, 50);
    ctx.fillStyle = '#22d3ee'; // Bright cyan
    ctx.fillText('UP', 128, 50);

    // DOWN text with thick black outline
    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8; // Thicker outline
    ctx.strokeText('DOWN', 128, 100);
    ctx.fillStyle = '#fb7185'; // Bright pink
    ctx.fillText('DOWN', 128, 100);

    const texture = new THREE.CanvasTexture(canvas);
    const labelGeometry = new THREE.PlaneGeometry(0.5, 0.25);
    const label = new THREE.Mesh(
      labelGeometry,
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(-0.65, 1.4, 0);
    label.rotation.y = Math.PI / 2;
    lifter.add(label);

    // State indicator with strong cartoon glow effect
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(1.3, 2.8, 1.3),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee, // Bright cyan
        transparent: true,
        opacity: 0.25,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.5,
        gradientMap: gradientMap
      })
    );
    indicator.position.y = 1.4;
    indicator.userData.isStateIndicator = true;
    lifter.add(indicator);

    // Scale to target height
    const bbox = new THREE.Box3().setFromObject(lifter);
    const actualHeight = bbox.max.y - bbox.min.y;
    const scale = targetHeight / actualHeight;
    lifter.scale.set(scale, scale, scale);

    return lifter;
  }

  static updateState(lifter, state) {
    // Bright cartoon colors for state
    const colors = {
      'R': 0x22d3ee,  // Bright cyan (Ready)
      'G': 0xfde047,  // Bright yellow (Going)
      'F': 0x60a5fa,  // Bright blue (Finish)
      'H': 0xa78bfa   // Bright purple (Homing)
    };
    const color = colors[state] || 0x22d3ee;

    lifter.traverse(function(child) {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  /**
   * Animate platform movement
   * @param {THREE.Group} lifter - Lifter object
   * @param {string} direction - 'UP' or 'DOWN'
   * @param {number} speed - Animation speed (default: 0.1)
   */
  static animate(lifter, direction, speed) {
    speed = speed || 0.1;

    // Find platform
    let platform = null;
    lifter.traverse(function(child) {
      if (child.userData.isLiftPlatform) {
        platform = child;
      }
    });

    if (!platform) return;

    // Get scale factor
    const scale = lifter.scale.y;

    // Define min/max positions (before scaling)
    const minY = 0.5;   // Bottom position
    const maxY = 2.4;   // Top position

    // Current position (unscaled)
    const currentY = platform.position.y / scale;

    // Target position
    let targetY = currentY;
    if (direction === 'UP') {
      targetY = maxY;
    } else if (direction === 'DOWN') {
      targetY = minY;
    }

    // Smooth interpolation
    const newY = currentY + (targetY - currentY) * speed;
    platform.position.y = newY * scale;

    return Math.abs(newY - targetY) < 0.01; // Return true if reached target
  }

  /**
   * Set platform position directly
   * @param {THREE.Group} lifter - Lifter object
   * @param {number} position - Position 0.0 (DOWN) to 1.0 (UP)
   */
  static setPosition(lifter, position) {
    position = Math.max(0, Math.min(1, position)); // Clamp 0-1

    let platform = null;
    lifter.traverse(function(child) {
      if (child.userData.isLiftPlatform) {
        platform = child;
      }
    });

    if (!platform) return;

    const scale = lifter.scale.y;
    const minY = 0.5;
    const maxY = 2.4;
    const targetY = minY + (maxY - minY) * position;

    platform.position.y = targetY * scale;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Lifter;
}

if (typeof window !== 'undefined') {
  window.Lifter = Lifter;
}
