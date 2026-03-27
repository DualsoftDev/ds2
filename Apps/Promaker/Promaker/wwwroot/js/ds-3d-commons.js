/**
 * DS 3D Commons
 * 케이블 스플라인, 파티클, 히트맵 셰이더 공통 컴포넌트
 */

window.DS3DCommons = (function() {
  'use strict';

  const gradients = {
    cool: ['#0ea5e9', '#22c55e', '#fbbf24', '#f97316'],
    warm: ['#1d4ed8', '#3b82f6', '#f59e0b', '#ef4444']
  };

  function createFlowTexture(base = '#3b82f6', accent = '#1d4ed8', background = '#0f172a') {
    const canvas = document.createElement('canvas');
    canvas.width = 128;
    canvas.height = 8;
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = background;
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    const stripeCount = 6;
    for (let i = 0; i < stripeCount; i++) {
      const start = (i / stripeCount) * canvas.width;
      const grad = ctx.createLinearGradient(start, 0, start + canvas.width / stripeCount, 0);
      grad.addColorStop(0, base);
      grad.addColorStop(1, accent);
      ctx.fillStyle = grad;
      ctx.fillRect(start, 0, canvas.width / stripeCount, canvas.height);
    }

    const texture = new THREE.CanvasTexture(canvas);
    texture.wrapS = THREE.RepeatWrapping;
    texture.wrapT = THREE.RepeatWrapping;
    texture.needsUpdate = true;
    return texture;
  }

  class CableSpline {
    constructor(config = {}) {
      const {
        points = [new THREE.Vector3(0, 0, 0), new THREE.Vector3(2, 0, 0)],
        color = 0x3b82f6,
        radius = 0.08,
        segments = 32,
        radialSegments = 12,
        flow = false,
        flowSpeed = 0.6,
        glow = 0.35
      } = config;

      this.curve = new THREE.CatmullRomCurve3(points.map(p => p.clone()));
      this.segments = segments;
      this.radius = radius;
      this.radialSegments = radialSegments;
      this.flowSpeed = flowSpeed;

      this.geometry = new THREE.TubeGeometry(this.curve, segments, radius, radialSegments, false);
      const materialOptions = {
        color: color,
        metalness: 0.35,
        roughness: 0.25,
        emissive: new THREE.Color(color),
        emissiveIntensity: glow
      };

      if (flow) {
        materialOptions.map = createFlowTexture();
        materialOptions.transparent = true;
        materialOptions.opacity = 0.9;
      }

      this.material = new THREE.MeshStandardMaterial(materialOptions);
      this.mesh = new THREE.Mesh(this.geometry, this.material);
      this.mesh.castShadow = true;
      this.mesh.receiveShadow = true;
    }

    setPoints(points) {
      this.curve.points = points.map(p => p.clone());
      const newGeo = new THREE.TubeGeometry(this.curve, this.segments, this.radius, this.radialSegments, false);
      this.geometry.dispose();
      this.mesh.geometry = newGeo;
      this.geometry = newGeo;
    }

    update(deltaTime = 0.016) {
      if (this.material.map) {
        this.material.map.offset.x -= this.flowSpeed * deltaTime;
      }
    }

    setHighlight(intensity = 0.6) {
      if (this.material.emissive) {
        this.material.emissiveIntensity = intensity;
      }
    }

    dispose() {
      this.geometry.dispose();
      this.material.dispose();
      if (this.material.map) this.material.map.dispose();
    }
  }

  class ParticleEmitter {
    constructor(scene, config = {}) {
      this.scene = scene;
      this.count = config.count || 120;
      this.emitter = config.emitter || new THREE.Vector3();
      this.direction = config.direction || new THREE.Vector3(0, 1, 0);
      this.spread = config.spread || 0.4;
      this.speed = config.speed || 1.2;
      this.gravity = config.gravity || -0.2;
      this.size = config.size || 0.08;
      this.color = config.color || 0x60a5fa;
      this.active = false;

      const positions = new Float32Array(this.count * 3);
      const velocities = new Float32Array(this.count * 3);

      for (let i = 0; i < this.count; i++) {
        positions[i * 3] = this.emitter.x;
        positions[i * 3 + 1] = this.emitter.y;
        positions[i * 3 + 2] = this.emitter.z;

        velocities[i * 3] = this.direction.x + (Math.random() - 0.5) * this.spread;
        velocities[i * 3 + 1] = this.direction.y * this.speed + (Math.random() - 0.5) * this.spread;
        velocities[i * 3 + 2] = this.direction.z + (Math.random() - 0.5) * this.spread;
      }

      this.geometry = new THREE.BufferGeometry();
      this.geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
      this.geometry.setAttribute('velocity', new THREE.BufferAttribute(velocities, 3));

      this.material = new THREE.PointsMaterial({
        color: this.color,
        size: this.size,
        transparent: true,
        opacity: 0.85,
        blending: THREE.AdditiveBlending,
        depthWrite: false
      });

      this.points = new THREE.Points(this.geometry, this.material);
      this.points.visible = false;
      this.scene.add(this.points);
    }

    start() {
      this.active = true;
      this.points.visible = true;
    }

    stop() {
      this.active = false;
      this.points.visible = false;
    }

    update(deltaTime = 0.016) {
      if (!this.active) return;

      const positions = this.geometry.attributes.position.array;
      const velocities = this.geometry.attributes.velocity.array;

      for (let i = 0; i < this.count; i++) {
        const idx = i * 3;
        positions[idx] += velocities[idx] * deltaTime;
        positions[idx + 1] += velocities[idx + 1] * deltaTime;
        positions[idx + 2] += velocities[idx + 2] * deltaTime;

        velocities[idx + 1] += this.gravity * deltaTime;

        if (positions[idx + 1] < this.emitter.y) {
          positions[idx] = this.emitter.x;
          positions[idx + 1] = this.emitter.y;
          positions[idx + 2] = this.emitter.z;

          velocities[idx] = this.direction.x + (Math.random() - 0.5) * this.spread;
          velocities[idx + 1] = this.direction.y * this.speed + (Math.random() - 0.5) * this.spread;
          velocities[idx + 2] = this.direction.z + (Math.random() - 0.5) * this.spread;
        }
      }

      this.geometry.attributes.position.needsUpdate = true;
      this.geometry.attributes.velocity.needsUpdate = true;
    }

    dispose() {
      this.scene.remove(this.points);
      this.geometry.dispose();
      this.material.dispose();
    }
  }

  class HeatmapPlane {
    constructor(config = {}) {
      const {
        size = { x: 6, y: 3 },
        resolution = { x: 24, y: 12 },
        gradient = gradients.cool,
        opacity = 0.82,
        gamma = 1.0,
        position = new THREE.Vector3(0, 0.01, 0),
        rotateToFloor = true
      } = config;

      this.resolution = resolution;
      this.opacity = opacity;
      this.gamma = gamma;
      this.data = new Uint8Array(resolution.x * resolution.y);

      this.texture = new THREE.DataTexture(this.data, resolution.x, resolution.y, THREE.LuminanceFormat);
      this.texture.needsUpdate = true;
      this.texture.minFilter = THREE.LinearFilter;
      this.texture.magFilter = THREE.LinearFilter;

      this.material = new THREE.ShaderMaterial({
        transparent: true,
        depthWrite: false,
        uniforms: {
          uData: { value: this.texture },
          uOpacity: { value: opacity },
          uGamma: { value: gamma },
          uColorA: { value: new THREE.Color(gradient[0]) },
          uColorB: { value: new THREE.Color(gradient[1] || gradient[0]) },
          uColorC: { value: new THREE.Color(gradient[2] || gradient[1] || gradient[0]) },
          uColorD: { value: new THREE.Color(gradient[3] || gradient[2] || gradient[1] || gradient[0]) }
        },
        vertexShader: `
          varying vec2 vUv;
          void main() {
            vUv = uv;
            gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
          }
        `,
        fragmentShader: `
          uniform sampler2D uData;
          uniform float uOpacity;
          uniform float uGamma;
          uniform vec3 uColorA;
          uniform vec3 uColorB;
          uniform vec3 uColorC;
          uniform vec3 uColorD;
          varying vec2 vUv;

          vec3 getGradient(float t) {
            if (t < 0.333) {
              return mix(uColorA, uColorB, t / 0.333);
            } else if (t < 0.666) {
              return mix(uColorB, uColorC, (t - 0.333) / 0.333);
            }
            return mix(uColorC, uColorD, (t - 0.666) / 0.334);
          }

          void main() {
            float raw = texture2D(uData, vUv).r;
            float t = clamp(pow(raw, uGamma), 0.0, 1.0);
            vec3 color = getGradient(t);
            float alpha = uOpacity * smoothstep(0.0, 0.05, t);
            gl_FragColor = vec4(color, alpha);
          }
        `
      });

      this.mesh = new THREE.Mesh(new THREE.PlaneGeometry(size.x, size.y), this.material);
      if (rotateToFloor) this.mesh.rotation.x = -Math.PI / 2;
      this.mesh.position.copy(position);
      this.mesh.renderOrder = 2;
    }

    setData(values, range = { min: 0, max: 1 }) {
      const min = range.min ?? 0;
      const max = range.max ?? 1;
      const span = Math.max(max - min, 1e-6);

      for (let i = 0; i < this.data.length; i++) {
        const v = values[i] ?? min;
        const n = Math.max(0, Math.min(1, (v - min) / span));
        this.data[i] = Math.floor(n * 255);
      }

      this.texture.needsUpdate = true;
    }

    setOpacity(value) {
      this.material.uniforms.uOpacity.value = value;
    }

    dispose() {
      this.mesh.geometry.dispose();
      this.material.dispose();
      this.texture.dispose();
    }
  }

  return {
    CableSpline,
    ParticleEmitter,
    HeatmapPlane,
    createFlowTexture,
    gradients
  };
})();
