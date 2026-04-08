/**
 * Robot_Gantry - 갠트리 로봇 (Cartoon Style)
 * 명령어: MOVE_X, MOVE_Y, MOVE_Z
 * 용도: 대형 워크스페이스, 정밀 가공
 * @version 1.0 - Cartoon/Toon rendering with XYZ gantry motion
 */

class Robot_Gantry {
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
      baseColor = 0xf59e0b,  // Amber
      targetHeight = 3.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const robot = new THREE.Group();
    robot.userData.deviceType = 'Robot_Gantry';
    robot.userData.position = { x: 0, y: 0, z: 0 };

    // ===== FRAME STRUCTURE =====
    // Vertical columns (4개)
    const columnGeometry = new THREE.BoxGeometry(0.2, 3.0, 0.2);
    const columnMaterial = new THREE.MeshToonMaterial({
      color: 0x64748b,
      gradientMap: gradientMap
    });

    const columnPositions = [
      [-2, 1.5, -1.5],
      [-2, 1.5, 1.5],
      [2, 1.5, -1.5],
      [2, 1.5, 1.5]
    ];

    columnPositions.forEach(pos => {
      const column = new THREE.Mesh(columnGeometry.clone(), columnMaterial);
      column.position.set(...pos);
      column.castShadow = true;
      robot.add(column);
      const columnOutline = this.createOutline(columnGeometry.clone(), 0x000000, 1.15);
      columnOutline.position.set(...pos);
      robot.add(columnOutline);
    });

    // Top X-axis beam
    const xBeamGeometry = new THREE.BoxGeometry(4.4, 0.25, 0.3);
    const xBeam = new THREE.Mesh(
      xBeamGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.2
      })
    );
    xBeam.position.set(0, 3.0, 0);
    xBeam.castShadow = true;
    robot.add(xBeam);
    const xBeamOutline = this.createOutline(xBeamGeometry.clone(), 0x000000, 1.15);
    xBeamOutline.position.set(0, 3.0, 0);
    robot.add(xBeamOutline);

    // ===== Y-AXIS CARRIAGE (이동 캐리지) =====
    const yCarriageGroup = new THREE.Group();
    yCarriageGroup.position.set(0, 3.0, 0);
    yCarriageGroup.userData.isYCarriage = true;
    robot.add(yCarriageGroup);

    const yCarriageGeometry = new THREE.BoxGeometry(0.4, 0.35, 0.6);
    const yCarriage = new THREE.Mesh(
      yCarriageGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.4
      })
    );
    yCarriage.castShadow = true;
    yCarriageGroup.add(yCarriage);
    const yCarriageOutline = this.createOutline(yCarriageGeometry.clone(), 0x000000, 1.18);
    yCarriageGroup.add(yCarriageOutline);

    // ===== Z-AXIS SPINDLE =====
    const zSpindleGroup = new THREE.Group();
    zSpindleGroup.position.y = -1.2;
    zSpindleGroup.userData.isZSpindle = true;
    yCarriageGroup.add(zSpindleGroup);

    const spindleGeometry = new THREE.CylinderGeometry(0.12, 0.12, 1.8, 16);
    const spindle = new THREE.Mesh(
      spindleGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    spindle.castShadow = true;
    zSpindleGroup.add(spindle);
    const spindleOutline = this.createOutline(spindleGeometry.clone(), 0x000000, 1.15);
    zSpindleGroup.add(spindleOutline);

    // Tool head
    const toolGeometry = new THREE.ConeGeometry(0.25, 0.4, 16);
    const tool = new THREE.Mesh(
      toolGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.5
      })
    );
    tool.position.y = -1.1;
    tool.castShadow = true;
    zSpindleGroup.add(tool);
    const toolOutline = this.createOutline(toolGeometry.clone(), 0x000000, 1.20);
    toolOutline.position.y = -1.1;
    zSpindleGroup.add(toolOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#f59e0b';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 36px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('GANTRY', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('GANTRY', 128, 50);

    ctx.font = 'bold 28px Arial';
    ctx.strokeText('ROBOT', 128, 95);
    ctx.fillStyle = '#f59e0b';
    ctx.fillText('ROBOT', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(1.0, 0.5),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 2.0, 0);
    robot.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(5.0, 3.5, 3.5),
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
    robot.add(indicator);

    const bbox = new THREE.Box3().setFromObject(robot);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    robot.scale.set(scale, scale, scale);

    return robot;
  }

  static updateState(robot, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    robot.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(robot, direction, speed) {
    speed = speed || 0.02;

    let yCarriage = null;
    let zSpindle = null;

    robot.traverse(child => {
      if (child.userData.isYCarriage) yCarriage = child;
      if (child.userData.isZSpindle) zSpindle = child;
    });

    if (!robot.userData.position) {
      robot.userData.position = { x: 0, y: 0, z: 0 };
    }
    if (!robot.userData.direction) {
      robot.userData.direction = { x: 1, y: 1, z: 1 };
    }

    const pos = robot.userData.position;
    const dir = robot.userData.direction;

    if (direction === 'MOVE_X') {
      // X-axis movement (left-right)
      if (yCarriage) {
        pos.x += speed * dir.x * 2;
        if (pos.x >= 1.5) {
          pos.x = 1.5;
          dir.x = -1;
        } else if (pos.x <= -1.5) {
          pos.x = -1.5;
          dir.x = 1;
        }
        yCarriage.position.x = pos.x;
      }
    } else if (direction === 'MOVE_Y') {
      // Y-axis movement (forward-backward)
      if (yCarriage) {
        pos.y += speed * dir.y * 2;
        if (pos.y >= 1.0) {
          pos.y = 1.0;
          dir.y = -1;
        } else if (pos.y <= -1.0) {
          pos.y = -1.0;
          dir.y = 1;
        }
        yCarriage.position.z = pos.y;
      }
    } else if (direction === 'MOVE_Z') {
      // Z-axis movement (up-down)
      if (zSpindle) {
        pos.z += speed * dir.z * 2;
        if (pos.z >= 0.5) {
          pos.z = 0.5;
          dir.z = -1;
        } else if (pos.z <= -0.5) {
          pos.z = -0.5;
          dir.z = 1;
        }
        zSpindle.position.y = -1.2 + pos.z;
      }
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Robot_Gantry;
}

if (typeof window !== 'undefined') {
  window.Robot_Gantry = Robot_Gantry;
}
