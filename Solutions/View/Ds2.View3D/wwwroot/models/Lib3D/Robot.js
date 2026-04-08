/**
 * Robot - 6축 산업용 로봇 (Cartoon Style)
 * 명령어: CMD1, CMD2, HOME
 * 용도: 용접, 핸들링, 조립
 * @version 2.1 - Enhanced animation with clearly different CMD1 and CMD2 movements
 */

class Robot {
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
      side: THREE.BackSide
    });
    const outline = new THREE.Mesh(geometry, outlineMaterial);
    outline.scale.multiplyScalar(thickness);
    outline.userData.isOutline = true;
    return outline;
  }

  static create(THREE, options = {}) {
    const {
      baseColor = 0xfbbf24,  // Bright amber
      armColor = 0xfde047,   // Bright yellow
      targetHeight = 3.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const robot = new THREE.Group();
    robot.userData.deviceType = 'Robot';
    robot.userData.jointAngles = { base: 0, shoulder: 0, elbow: 0 };

    // ===== BASE PLATFORM =====
    const platformGeometry = new THREE.CylinderGeometry(1.2, 1.2, 0.3, 32);
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

    // ===== BASE ROTATION GROUP (회전 가능) =====
    const baseGroup = new THREE.Group();
    baseGroup.position.y = 0.3;
    baseGroup.userData.isBaseJoint = true;
    robot.add(baseGroup);

    // Joint 1: Base rotation housing
    const j1Geometry = new THREE.CylinderGeometry(0.8, 0.9, 0.6, 24);
    const j1 = new THREE.Mesh(
      j1Geometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.2
      })
    );
    j1.position.y = 0.3;
    j1.castShadow = true;
    baseGroup.add(j1);
    const j1Outline = this.createOutline(j1Geometry.clone(), 0x000000, 1.15);
    j1Outline.position.y = 0.3;
    baseGroup.add(j1Outline);

    // ===== SHOULDER GROUP (회전 가능) =====
    const shoulderGroup = new THREE.Group();
    shoulderGroup.position.y = 0.85;
    shoulderGroup.userData.isShoulderJoint = true;
    baseGroup.add(shoulderGroup);

    // Joint 2: Shoulder
    const j2Geometry = new THREE.BoxGeometry(0.6, 0.5, 0.6);
    const j2 = new THREE.Mesh(
      j2Geometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.2
      })
    );
    j2.castShadow = true;
    shoulderGroup.add(j2);
    const j2Outline = this.createOutline(j2Geometry.clone(), 0x000000, 1.15);
    shoulderGroup.add(j2Outline);

    // Upper arm
    const upperArmGeometry = new THREE.BoxGeometry(0.35, 1.2, 0.35);
    const upperArm = new THREE.Mesh(
      upperArmGeometry,
      new THREE.MeshToonMaterial({
        color: armColor,
        gradientMap: gradientMap,
        emissive: armColor,
        emissiveIntensity: 0.15
      })
    );
    upperArm.position.set(0.3, 0.65, 0);
    upperArm.rotation.z = -0.3;
    upperArm.castShadow = true;
    shoulderGroup.add(upperArm);
    const upperArmOutline = this.createOutline(upperArmGeometry.clone(), 0x000000, 1.18);
    upperArmOutline.position.set(0.3, 0.65, 0);
    upperArmOutline.rotation.z = -0.3;
    shoulderGroup.add(upperArmOutline);

    // ===== ELBOW GROUP (회전 가능) =====
    const elbowGroup = new THREE.Group();
    elbowGroup.position.set(0.65, 1.15, 0);
    elbowGroup.userData.isElbowJoint = true;
    shoulderGroup.add(elbowGroup);

    // Joint 3: Elbow
    const j3Geometry = new THREE.SphereGeometry(0.3, 16, 16);
    const j3 = new THREE.Mesh(
      j3Geometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    j3.castShadow = true;
    elbowGroup.add(j3);
    const j3Outline = this.createOutline(j3Geometry.clone(), 0x000000, 1.15);
    elbowGroup.add(j3Outline);

    // Forearm
    const forearmGeometry = new THREE.BoxGeometry(0.3, 1.0, 0.3);
    const forearm = new THREE.Mesh(
      forearmGeometry,
      new THREE.MeshToonMaterial({
        color: armColor,
        gradientMap: gradientMap,
        emissive: armColor,
        emissiveIntensity: 0.15
      })
    );
    forearm.position.set(0, 0.5, 0);
    forearm.castShadow = true;
    elbowGroup.add(forearm);
    const forearmOutline = this.createOutline(forearmGeometry.clone(), 0x000000, 1.18);
    forearmOutline.position.set(0, 0.5, 0);
    elbowGroup.add(forearmOutline);

    // Wrist assembly
    const wristGeometry = new THREE.CylinderGeometry(0.2, 0.2, 0.4, 16);
    const wrist = new THREE.Mesh(
      wristGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8,
        gradientMap: gradientMap
      })
    );
    wrist.position.set(0, 1.15, 0);
    wrist.castShadow = true;
    elbowGroup.add(wrist);
    const wristOutline = this.createOutline(wristGeometry.clone(), 0x000000, 1.15);
    wristOutline.position.set(0, 1.15, 0);
    elbowGroup.add(wristOutline);

    // Tool flange
    const flangeGeometry = new THREE.CylinderGeometry(0.25, 0.25, 0.12, 20);
    const flange = new THREE.Mesh(
      flangeGeometry,
      new THREE.MeshToonMaterial({
        color: 0x475569,
        gradientMap: gradientMap
      })
    );
    flange.position.set(0, 1.38, 0);
    flange.castShadow = true;
    elbowGroup.add(flange);
    const flangeOutline = this.createOutline(flangeGeometry.clone(), 0x000000, 1.15);
    flangeOutline.position.set(0, 1.38, 0);
    elbowGroup.add(flangeOutline);

    // Gripper
    const gripperGeometry = new THREE.BoxGeometry(0.4, 0.15, 0.3);
    const gripper = new THREE.Mesh(
      gripperGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.4
      })
    );
    gripper.position.set(0, 1.52, 0);
    gripper.castShadow = true;
    elbowGroup.add(gripper);
    const gripperOutline = this.createOutline(gripperGeometry.clone(), 0x000000, 1.18);
    gripperOutline.position.set(0, 1.52, 0);
    elbowGroup.add(gripperOutline);

    // Label
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#fbbf24';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('6-AXIS', 128, 45);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('6-AXIS', 128, 45);

    ctx.strokeText('ROBOT', 128, 90);
    ctx.fillStyle = '#fbbf24';
    ctx.fillText('ROBOT', 128, 90);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.5, 0.65);
    robot.add(label);

    // State indicator
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(2.5, 3.5, 2.5),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.y = 1.75;
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
   * Animate robot movement with clear distinction between CMD1 and CMD2
   */
  static animate(robot, direction, speed) {
    speed = speed || 0.03;

    let baseJoint = null;
    let shoulderJoint = null;
    let elbowJoint = null;

    robot.traverse(child => {
      if (child.userData.isBaseJoint) baseJoint = child;
      if (child.userData.isShoulderJoint) shoulderJoint = child;
      if (child.userData.isElbowJoint) elbowJoint = child;
    });

    if (!robot.userData.jointAngles) {
      robot.userData.jointAngles = { base: 0, shoulder: 0, elbow: 0 };
    }

    const angles = robot.userData.jointAngles;

    if (direction === 'CMD1') {
      // CMD1: 베이스 회전 (360도 연속 회전)
      angles.base += speed * 1.5;
      if (baseJoint) baseJoint.rotation.y = angles.base;
    } else if (direction === 'CMD2') {
      // CMD2: 어깨+팔꿈치 동시 동작 (팔 펴고 접기)
      angles.shoulder += speed;
      angles.elbow += speed * 1.3;
      if (shoulderJoint) shoulderJoint.rotation.z = Math.sin(angles.shoulder) * 0.6;
      if (elbowJoint) elbowJoint.rotation.z = Math.sin(angles.elbow) * 0.8;
    } else if (direction === 'HOME') {
      // Return to home position
      angles.base *= 0.9;
      angles.shoulder *= 0.9;
      angles.elbow *= 0.9;
      if (baseJoint) baseJoint.rotation.y = angles.base;
      if (shoulderJoint) shoulderJoint.rotation.z = Math.sin(angles.shoulder) * 0.6;
      if (elbowJoint) elbowJoint.rotation.z = Math.sin(angles.elbow) * 0.8;

      if (Math.abs(angles.base) < 0.01 && Math.abs(angles.shoulder) < 0.01 && Math.abs(angles.elbow) < 0.01) {
        angles.base = 0;
        angles.shoulder = 0;
        angles.elbow = 0;
        if (baseJoint) baseJoint.rotation.y = 0;
        if (shoulderJoint) shoulderJoint.rotation.z = 0;
        if (elbowJoint) elbowJoint.rotation.z = 0;
        return true;
      }
    }

    return false; // Continue animation
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Robot;
}

if (typeof window !== 'undefined') {
  window.Robot = Robot;
}
