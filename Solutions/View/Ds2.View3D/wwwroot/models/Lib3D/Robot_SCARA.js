/**
 * Robot_SCARA - SCARA 로봇 (Cartoon Style)
 * 명령어: POS1, POS2, HOME
 * - POS1: 오른쪽으로 90도 회전 (+90°) 후 정지
 * - POS2: 왼쪽으로 90도 회전 (-90°) 후 정지
 * - HOME: 원위치 (0°) 후 정지
 * 용도: 픽앤플레이스, 조립
 * @version 3.0 - Complete rewrite with proper stop mechanism
 */

class Robot_SCARA {
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
      baseColor = 0xa78bfa,  // Bright purple
      armColor = 0xc084fc,   // Light purple
      targetHeight = 2.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const robot = new THREE.Group();
    robot.userData.deviceType = 'Robot_SCARA';

    // 애니메이션 상태 초기화
    robot.userData.currentAngle = 0;
    robot.userData.targetAngle = 0;
    robot.userData.isMoving = false;

    // ===== BASE PLATFORM =====
    const platformGeometry = new THREE.CylinderGeometry(0.9, 0.9, 0.3, 32);
    const platform = new THREE.Mesh(
      platformGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    platform.position.y = 0.15;
    platform.castShadow = true;
    robot.add(platform);
    const platformOutline = this.createOutline(platformGeometry.clone(), 0x000000, 1.12);
    platformOutline.position.y = 0.15;
    robot.add(platformOutline);

    // ===== VERTICAL COLUMN =====
    const columnGeometry = new THREE.CylinderGeometry(0.3, 0.35, 0.6, 24);
    const column = new THREE.Mesh(
      columnGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.2
      })
    );
    column.position.y = 0.6;
    column.castShadow = true;
    robot.add(column);
    const columnOutline = this.createOutline(columnGeometry.clone(), 0x000000, 1.15);
    columnOutline.position.y = 0.6;
    robot.add(columnOutline);

    // ===== JOINT 1 (회전 가능) =====
    const joint1Group = new THREE.Group();
    joint1Group.position.y = 1.05;
    joint1Group.userData.isJoint1 = true;
    robot.add(joint1Group);

    // Joint 1 housing
    const j1HousingGeometry = new THREE.CylinderGeometry(0.38, 0.38, 0.3, 24);
    const j1Housing = new THREE.Mesh(
      j1HousingGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.4
      })
    );
    j1Housing.castShadow = true;
    joint1Group.add(j1Housing);
    const j1HousingOutline = this.createOutline(j1HousingGeometry.clone(), 0x000000, 1.18);
    joint1Group.add(j1HousingOutline);

    // Arm 1
    const arm1Geometry = new THREE.BoxGeometry(1.6, 0.16, 0.28);
    const arm1 = new THREE.Mesh(
      arm1Geometry,
      new THREE.MeshToonMaterial({
        color: armColor,
        gradientMap: gradientMap,
        emissive: armColor,
        emissiveIntensity: 0.2
      })
    );
    arm1.position.set(0.8, 0.25, 0);
    arm1.castShadow = true;
    joint1Group.add(arm1);
    const arm1Outline = this.createOutline(arm1Geometry.clone(), 0x000000, 1.18);
    arm1Outline.position.set(0.8, 0.25, 0);
    joint1Group.add(arm1Outline);

    // ===== JOINT 2 (두 번째 회전 관절) =====
    const joint2Group = new THREE.Group();
    joint2Group.position.set(1.6, 0.25, 0);
    joint2Group.userData.isJoint2 = true;
    joint1Group.add(joint2Group);

    // Joint 2 housing
    const j2HousingGeometry = new THREE.CylinderGeometry(0.32, 0.32, 0.28, 20);
    const j2Housing = new THREE.Mesh(
      j2HousingGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.5
      })
    );
    j2Housing.castShadow = true;
    joint2Group.add(j2Housing);
    const j2HousingOutline = this.createOutline(j2HousingGeometry.clone(), 0x000000, 1.18);
    joint2Group.add(j2HousingOutline);

    // Arm 2
    const arm2Geometry = new THREE.BoxGeometry(1.2, 0.14, 0.24);
    const arm2 = new THREE.Mesh(
      arm2Geometry,
      new THREE.MeshToonMaterial({
        color: armColor,
        gradientMap: gradientMap,
        emissive: armColor,
        emissiveIntensity: 0.2
      })
    );
    arm2.position.set(0.6, 0.2, 0);
    arm2.castShadow = true;
    joint2Group.add(arm2);
    const arm2Outline = this.createOutline(arm2Geometry.clone(), 0x000000, 1.18);
    arm2Outline.position.set(0.6, 0.2, 0);
    joint2Group.add(arm2Outline);

    // ===== Z-AXIS =====
    const zAxisGeometry = new THREE.CylinderGeometry(0.12, 0.12, 0.8, 16);
    const zAxis = new THREE.Mesh(
      zAxisGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    zAxis.position.set(1.2, -0.25, 0);
    zAxis.castShadow = true;
    joint2Group.add(zAxis);
    const zAxisOutline = this.createOutline(zAxisGeometry.clone(), 0x000000, 1.15);
    zAxisOutline.position.set(1.2, -0.25, 0);
    joint2Group.add(zAxisOutline);

    // ===== END EFFECTOR =====
    const effectorGeometry = new THREE.CylinderGeometry(0.18, 0.18, 0.12, 20);
    const effector = new THREE.Mesh(
      effectorGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.5
      })
    );
    effector.position.set(1.2, -0.72, 0);
    effector.castShadow = true;
    joint2Group.add(effector);
    const effectorOutline = this.createOutline(effectorGeometry.clone(), 0x000000, 1.20);
    effectorOutline.position.set(1.2, -0.72, 0);
    joint2Group.add(effectorOutline);

    // Gripper fingers
    const fingerGeometry = new THREE.BoxGeometry(0.07, 0.2, 0.07);
    [-0.1, 0.1].forEach(x => {
      const finger = new THREE.Mesh(
        fingerGeometry.clone(),
        new THREE.MeshToonMaterial({
          color: 0x64748b,
          gradientMap: gradientMap
        })
      );
      finger.position.set(1.2 + x, -0.88, 0);
      finger.castShadow = true;
      joint2Group.add(finger);
      const fingerOutline = this.createOutline(fingerGeometry.clone(), 0x000000, 1.20);
      fingerOutline.position.set(1.2 + x, -0.88, 0);
      joint2Group.add(fingerOutline);
    });

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#a78bfa';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('SCARA', 128, 45);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('SCARA', 128, 45);

    ctx.strokeText('ROBOT', 128, 90);
    ctx.fillStyle = '#a78bfa';
    ctx.fillText('ROBOT', 128, 90);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.6, 0.3),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.6, 0.5);
    robot.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(3.5, 1.6, 1.3),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(1.3, 0.5, 0);
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

  /**
   * Animate SCARA robot
   * POS1: +90도 이동 후 정지
   * POS2: -90도 이동 후 정지
   * HOME: 0도 복귀 후 정지
   */
  static animate(robot, direction, speed) {
    speed = speed || 0.06;

    let joint1 = null;
    let joint2 = null;

    robot.traverse(child => {
      if (child.userData.isJoint1) joint1 = child;
      if (child.userData.isJoint2) joint2 = child;
    });

    if (!joint1 || !joint2) return false;

    const PI_2 = Math.PI / 2;

    // 목표 각도 설정
    let targetJ1 = 0;
    let targetJ2 = 0;

    if (direction === 'POS1') {
      targetJ1 = PI_2;        // +90도
      targetJ2 = -PI_2 * 0.6; // 보조 관절
    } else if (direction === 'POS2') {
      targetJ1 = -PI_2;       // -90도
      targetJ2 = PI_2 * 0.6;  // 보조 관절
    } else if (direction === 'HOME') {
      targetJ1 = 0;           // 0도
      targetJ2 = 0;           // 0도
    }

    // 현재 각도
    const currentJ1 = joint1.rotation.y;
    const currentJ2 = joint2.rotation.y;

    // 목표와의 차이
    const diff1 = targetJ1 - currentJ1;
    const diff2 = targetJ2 - currentJ2;

    // 목표에 도달했는지 확인 (0.01 라디안 = 약 0.57도)
    if (Math.abs(diff1) < 0.01 && Math.abs(diff2) < 0.01) {
      // 정확히 목표 각도로 설정
      joint1.rotation.y = targetJ1;
      joint2.rotation.y = targetJ2;
      return true; // 애니메이션 완료
    }

    // 부드럽게 이동 (easing)
    joint1.rotation.y += diff1 * speed;
    joint2.rotation.y += diff2 * speed;

    return false; // 아직 이동 중
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Robot_SCARA;
}

if (typeof window !== 'undefined') {
  window.Robot_SCARA = Robot_SCARA;
}
