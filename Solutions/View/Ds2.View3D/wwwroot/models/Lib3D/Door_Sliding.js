/**
 * Door_Sliding - 슬라이딩 도어 (Cartoon Style)
 * 명령어: OPEN, CLOSE, HOLD
 * 용도: 자동 출입문, 구역 차단
 * @version 1.0 - Cartoon/Toon rendering with sliding door motion
 */

class Door_Sliding {
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
      baseColor = 0x0891b2,  // Cyan
      targetHeight = 2.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const door = new THREE.Group();
    door.userData.deviceType = 'Door_Sliding';

    // ===== DOOR FRAME =====
    // Left frame
    const frameGeometry = new THREE.BoxGeometry(0.15, 2.2, 0.1);
    const frameMaterial = new THREE.MeshToonMaterial({
      color: 0x64748b,
      gradientMap: gradientMap
    });

    const leftFrame = new THREE.Mesh(frameGeometry.clone(), frameMaterial);
    leftFrame.position.set(-1.1, 1.1, 0);
    leftFrame.castShadow = true;
    door.add(leftFrame);
    const leftFrameOutline = this.createOutline(frameGeometry.clone(), 0x000000, 1.12);
    leftFrameOutline.position.set(-1.1, 1.1, 0);
    door.add(leftFrameOutline);

    // Right frame
    const rightFrame = new THREE.Mesh(frameGeometry.clone(), frameMaterial);
    rightFrame.position.set(1.1, 1.1, 0);
    rightFrame.castShadow = true;
    door.add(rightFrame);
    const rightFrameOutline = this.createOutline(frameGeometry.clone(), 0x000000, 1.12);
    rightFrameOutline.position.set(1.1, 1.1, 0);
    door.add(rightFrameOutline);

    // Top frame
    const topFrameGeometry = new THREE.BoxGeometry(2.4, 0.15, 0.1);
    const topFrame = new THREE.Mesh(topFrameGeometry, frameMaterial);
    topFrame.position.set(0, 2.275, 0);
    topFrame.castShadow = true;
    door.add(topFrame);
    const topFrameOutline = this.createOutline(topFrameGeometry.clone(), 0x000000, 1.12);
    topFrameOutline.position.set(0, 2.275, 0);
    door.add(topFrameOutline);

    // ===== LEFT DOOR PANEL =====
    const leftPanelGroup = new THREE.Group();
    leftPanelGroup.userData.isLeftPanel = true;
    door.add(leftPanelGroup);

    const panelGeometry = new THREE.BoxGeometry(1.0, 2.0, 0.08);
    const leftPanel = new THREE.Mesh(
      panelGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.2
      })
    );
    leftPanel.position.set(-0.5, 1.0, 0);
    leftPanel.castShadow = true;
    leftPanelGroup.add(leftPanel);
    const leftPanelOutline = this.createOutline(panelGeometry.clone(), 0x000000, 1.10);
    leftPanelOutline.position.set(-0.5, 1.0, 0);
    leftPanelGroup.add(leftPanelOutline);

    // Left panel handle
    const handleGeometry = new THREE.BoxGeometry(0.08, 0.3, 0.12);
    const handleMaterial = new THREE.MeshToonMaterial({
      color: 0xfbbf24,
      gradientMap: gradientMap,
      emissive: 0xf59e0b,
      emissiveIntensity: 0.4
    });

    const leftHandle = new THREE.Mesh(handleGeometry.clone(), handleMaterial);
    leftHandle.position.set(-0.15, 1.0, 0.08);
    leftHandle.castShadow = true;
    leftPanelGroup.add(leftHandle);

    // ===== RIGHT DOOR PANEL =====
    const rightPanelGroup = new THREE.Group();
    rightPanelGroup.userData.isRightPanel = true;
    door.add(rightPanelGroup);

    const rightPanel = new THREE.Mesh(
      panelGeometry.clone(),
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.2
      })
    );
    rightPanel.position.set(0.5, 1.0, 0);
    rightPanel.castShadow = true;
    rightPanelGroup.add(rightPanel);
    const rightPanelOutline = this.createOutline(panelGeometry.clone(), 0x000000, 1.10);
    rightPanelOutline.position.set(0.5, 1.0, 0);
    rightPanelGroup.add(rightPanelOutline);

    // Right panel handle
    const rightHandle = new THREE.Mesh(handleGeometry.clone(), handleMaterial);
    rightHandle.position.set(0.15, 1.0, 0.08);
    rightHandle.castShadow = true;
    rightPanelGroup.add(rightHandle);

    // ===== SENSORS =====
    const sensorGeometry = new THREE.BoxGeometry(0.12, 0.12, 0.12);
    const sensorMaterial = new THREE.MeshToonMaterial({
      color: 0xef4444,
      gradientMap: gradientMap,
      emissive: 0xdc2626,
      emissiveIntensity: 0.6
    });

    [-1.05, 1.05].forEach(x => {
      const sensor = new THREE.Mesh(sensorGeometry.clone(), sensorMaterial);
      sensor.position.set(x, 2.1, 0.08);
      sensor.castShadow = true;
      door.add(sensor);
    });

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#0891b2';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 36px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('SLIDING', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('SLIDING', 128, 50);

    ctx.font = 'bold 32px Arial';
    ctx.strokeText('DOOR', 128, 95);
    ctx.fillStyle = '#0891b2';
    ctx.fillText('DOOR', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.4, 0.1);
    door.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(2.5, 2.5, 0.3),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 1.1, 0);
    indicator.userData.isStateIndicator = true;
    door.add(indicator);

    const bbox = new THREE.Box3().setFromObject(door);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    door.scale.set(scale, scale, scale);

    return door;
  }

  static updateState(door, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    door.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(door, direction, speed) {
    speed = speed || 0.04;

    let leftPanel = null;
    let rightPanel = null;

    door.traverse(child => {
      if (child.userData.isLeftPanel) leftPanel = child;
      if (child.userData.isRightPanel) rightPanel = child;
    });

    if (!leftPanel || !rightPanel) return false;

    if (direction === 'OPEN') {
      // Open doors (slide apart)
      const targetLeft = -0.95;
      const targetRight = 0.95;

      leftPanel.position.x += (targetLeft - leftPanel.position.x) * speed;
      rightPanel.position.x += (targetRight - rightPanel.position.x) * speed;

      return Math.abs(leftPanel.position.x - targetLeft) < 0.01 &&
             Math.abs(rightPanel.position.x - targetRight) < 0.01;
    } else if (direction === 'CLOSE') {
      // Close doors (slide together)
      const targetLeft = 0;
      const targetRight = 0;

      leftPanel.position.x += (targetLeft - leftPanel.position.x) * speed;
      rightPanel.position.x += (targetRight - rightPanel.position.x) * speed;

      return Math.abs(leftPanel.position.x - targetLeft) < 0.01 &&
             Math.abs(rightPanel.position.x - targetRight) < 0.01;
    } else if (direction === 'HOLD') {
      // Hold position
      return false;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Door_Sliding;
}

if (typeof window !== 'undefined') {
  window.Door_Sliding = Door_Sliding;
}
