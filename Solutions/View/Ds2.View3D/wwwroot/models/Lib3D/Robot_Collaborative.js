/**
 * Robot_Collaborative - 협동 로봇 (Cartoon Style)
 * 명령어: ASSIST, WORK, HOME
 * 용도: 인간-로봇 협업, 조립
 * @version 1.0 - Cartoon/Toon rendering with collaborative robot animation
 */

class Robot_Collaborative {
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
      baseColor = 0x3b82f6,  // Blue
      targetHeight = 2.8
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const robot = new THREE.Group();
    robot.userData.deviceType = 'Robot_Collaborative';
    robot.userData.jointAngles = { shoulder: 0, elbow: 0, wrist: 0 };

    // ===== BASE =====
    const baseGeometry = new THREE.CylinderGeometry(0.6, 0.7, 0.4, 24);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.2;
    base.castShadow = true;
    robot.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.12);
    baseOutline.position.y = 0.2;
    robot.add(baseOutline);

    // ===== SHOULDER GROUP =====
    const shoulderGroup = new THREE.Group();
    shoulderGroup.position.y = 0.5;
    shoulderGroup.userData.isShoulderJoint = true;
    robot.add(shoulderGroup);

    // Joint 1
    const j1Geometry = new THREE.SphereGeometry(0.35, 16, 16);
    const j1 = new THREE.Mesh(
      j1Geometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    j1.castShadow = true;
    shoulderGroup.add(j1);
    const j1Outline = this.createOutline(j1Geometry.clone(), 0x000000, 1.15);
    shoulderGroup.add(j1Outline);

    // Upper arm
    const upperArmGeometry = new THREE.CylinderGeometry(0.18, 0.18, 0.9, 16);
    const upperArm = new THREE.Mesh(
      upperArmGeometry,
      new THREE.MeshToonMaterial({
        color: 0x93c5fd,
        gradientMap: gradientMap,
        emissive: 0x60a5fa,
        emissiveIntensity: 0.2
      })
    );
    upperArm.position.y = 0.5;
    upperArm.castShadow = true;
    shoulderGroup.add(upperArm);
    const upperArmOutline = this.createOutline(upperArmGeometry.clone(), 0x000000, 1.15);
    upperArmOutline.position.y = 0.5;
    shoulderGroup.add(upperArmOutline);

    // ===== ELBOW GROUP =====
    const elbowGroup = new THREE.Group();
    elbowGroup.position.y = 0.95;
    elbowGroup.userData.isElbowJoint = true;
    shoulderGroup.add(elbowGroup);

    // Joint 2
    const j2Geometry = new THREE.SphereGeometry(0.28, 16, 16);
    const j2 = new THREE.Mesh(
      j2Geometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    j2.castShadow = true;
    elbowGroup.add(j2);
    const j2Outline = this.createOutline(j2Geometry.clone(), 0x000000, 1.15);
    elbowGroup.add(j2Outline);

    // Forearm
    const forearmGeometry = new THREE.CylinderGeometry(0.15, 0.15, 0.8, 16);
    const forearm = new THREE.Mesh(
      forearmGeometry,
      new THREE.MeshToonMaterial({
        color: 0x93c5fd,
        gradientMap: gradientMap,
        emissive: 0x60a5fa,
        emissiveIntensity: 0.2
      })
    );
    forearm.position.y = 0.45;
    forearm.castShadow = true;
    elbowGroup.add(forearm);
    const forearmOutline = this.createOutline(forearmGeometry.clone(), 0x000000, 1.15);
    forearmOutline.position.y = 0.45;
    elbowGroup.add(forearmOutline);

    // ===== WRIST GROUP =====
    const wristGroup = new THREE.Group();
    wristGroup.position.y = 0.85;
    wristGroup.userData.isWristJoint = true;
    elbowGroup.add(wristGroup);

    // Wrist
    const wristGeometry = new THREE.SphereGeometry(0.22, 16, 16);
    const wrist = new THREE.Mesh(
      wristGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    wrist.castShadow = true;
    wristGroup.add(wrist);
    const wristOutline = this.createOutline(wristGeometry.clone(), 0x000000, 1.15);
    wristGroup.add(wristOutline);

    // End effector
    const effectorGeometry = new THREE.CylinderGeometry(0.15, 0.18, 0.3, 12);
    const effector = new THREE.Mesh(
      effectorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.4
      })
    );
    effector.position.y = 0.25;
    effector.castShadow = true;
    wristGroup.add(effector);
    const effectorOutline = this.createOutline(effectorGeometry.clone(), 0x000000, 1.18);
    effectorOutline.position.y = 0.25;
    wristGroup.add(effectorOutline);

    // Gripper fingers
    const fingerGeometry = new THREE.BoxGeometry(0.08, 0.25, 0.08);
    [-0.1, 0.1].forEach(x => {
      const finger = new THREE.Mesh(
        fingerGeometry.clone(),
        new THREE.MeshToonMaterial({
          color: 0xfbbf24,
          gradientMap: gradientMap
        })
      );
      finger.position.set(x, 0.47, 0);
      finger.castShadow = true;
      wristGroup.add(finger);
      const fingerOutline = this.createOutline(fingerGeometry.clone(), 0x000000, 1.20);
      fingerOutline.position.set(x, 0.47, 0);
      wristGroup.add(fingerOutline);
    });

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#3b82f6';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 30px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('COBOT', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('COBOT', 128, 50);

    ctx.font = 'bold 24px Arial';
    ctx.strokeText('COLLABORATIVE', 128, 90);
    ctx.fillStyle = '#3b82f6';
    ctx.fillText('COLLABORATIVE', 128, 90);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.7, 0.35),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.4, 0.45);
    robot.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(1.8, 3.0, 1.8),
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
    speed = speed || 0.04;

    let shoulderJoint = null;
    let elbowJoint = null;
    let wristJoint = null;

    robot.traverse(child => {
      if (child.userData.isShoulderJoint) shoulderJoint = child;
      if (child.userData.isElbowJoint) elbowJoint = child;
      if (child.userData.isWristJoint) wristJoint = child;
    });

    if (!robot.userData.jointAngles) {
      robot.userData.jointAngles = { shoulder: 0, elbow: 0, wrist: 0 };
    }

    const angles = robot.userData.jointAngles;

    if (direction === 'ASSIST') {
      // Gentle assisting motion
      angles.shoulder += speed;
      angles.elbow += speed * 1.2;
      angles.wrist += speed * 0.8;

      if (shoulderJoint) shoulderJoint.rotation.z = Math.sin(angles.shoulder) * 0.4;
      if (elbowJoint) elbowJoint.rotation.z = Math.sin(angles.elbow) * 0.5;
      if (wristJoint) wristJoint.rotation.x = Math.sin(angles.wrist) * 0.3;
    } else if (direction === 'WORK') {
      // Working motion
      angles.shoulder += speed * 1.5;
      angles.elbow += speed * 1.8;
      angles.wrist += speed * 1.2;

      if (shoulderJoint) shoulderJoint.rotation.z = Math.sin(angles.shoulder) * 0.6;
      if (elbowJoint) elbowJoint.rotation.z = Math.sin(angles.elbow) * 0.7;
      if (wristJoint) wristJoint.rotation.x = Math.sin(angles.wrist) * 0.5;
    } else if (direction === 'HOME') {
      // Return to home
      angles.shoulder *= 0.92;
      angles.elbow *= 0.92;
      angles.wrist *= 0.92;

      if (shoulderJoint) shoulderJoint.rotation.z = Math.sin(angles.shoulder) * 0.4;
      if (elbowJoint) elbowJoint.rotation.z = Math.sin(angles.elbow) * 0.5;
      if (wristJoint) wristJoint.rotation.x = Math.sin(angles.wrist) * 0.3;

      if (Math.abs(angles.shoulder) < 0.01 && Math.abs(angles.elbow) < 0.01 && Math.abs(angles.wrist) < 0.01) {
        angles.shoulder = 0;
        angles.elbow = 0;
        angles.wrist = 0;
        if (shoulderJoint) shoulderJoint.rotation.z = 0;
        if (elbowJoint) elbowJoint.rotation.z = 0;
        if (wristJoint) wristJoint.rotation.x = 0;
        return true;
      }
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Robot_Collaborative;
}

if (typeof window !== 'undefined') {
  window.Robot_Collaborative = Robot_Collaborative;
}
