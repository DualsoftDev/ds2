// Ev2 Dashboard - Three.js Interop
// 3D Facility Viewer for automotive manufacturing line

const Ev23DViewer = {
    // Active scene instances (keyed by elementId)
    _scenes: {},

    // RGFH color scheme (matching dashboard)
    stateColors: {
        R: { hex: 0x10b981, name: 'Ready' },     // Green
        G: { hex: 0xeab308, name: 'Going' },     // Yellow
        F: { hex: 0x3b82f6, name: 'Finish' },    // Blue
        H: { hex: 0x6b7280, name: 'Homing' }     // Gray
    },

    /**
     * Initialize 3D scene in container
     * @param {string} elementId - Container DOM id
     * @param {object} config - Scene configuration { works: [{id, name, position}] }
     * @returns {boolean} Success status
     */
    init: function(elementId, config = {}) {
        const container = document.getElementById(elementId);
        if (!container) {
            console.error(`Container element '${elementId}' not found`);
            return false;
        }

        // Check if Three.js is loaded
        if (typeof THREE === 'undefined') {
            console.error('Three.js is not loaded');
            return false;
        }

        try {
            // Debug: Log config received
            console.log('=== Ev23DViewer.init ===');
            console.log('Config:', config);
            console.log('Works count:', config.works ? config.works.length : 0);
            console.log('FlowZones count:', config.flowZones ? config.flowZones.length : 0);
            console.log('Connections count:', config.connections ? config.connections.length : 0);

            // Note: Empty works is allowed for drag-and-drop mode
            if (!config.works || config.works.length === 0) {
                console.log('ℹ️ Initializing empty scene (drag-and-drop mode)');
            }

            // Scene setup
            const scene = new THREE.Scene();
            scene.background = new THREE.Color(0x0f172a);
            scene.fog = new THREE.Fog(0x0f172a, 100, 300); // Increased fog range for larger scenes

            // Camera
            const camera = new THREE.PerspectiveCamera(
                50,
                container.clientWidth / container.clientHeight,
                0.1,
                1000
            );
            camera.position.set(0, 15, 35);
            camera.lookAt(0, 0, 0);

            // Renderer
            const renderer = new THREE.WebGLRenderer({ antialias: true });
            renderer.setSize(container.clientWidth, container.clientHeight);
            renderer.shadowMap.enabled = true;
            renderer.shadowMap.type = THREE.PCFSoftShadowMap;
            container.appendChild(renderer.domElement);

            // OrbitControls
            const controls = new THREE.OrbitControls(camera, renderer.domElement);
            controls.enableDamping = true;
            controls.dampingFactor = 0.05;
            controls.maxPolarAngle = Math.PI / 2;
            controls.minDistance = 10; // Prevent camera from going inside objects
            controls.maxDistance = 300; // Limit maximum zoom out
            controls.target.set(0, 0, 0);

            // Mouse button mapping
            // LEFT: Rotate camera
            // MIDDLE: Pan (move camera position)
            // RIGHT: Pan (alternative)
            // WHEEL: Zoom
            controls.mouseButtons = {
                LEFT: THREE.MOUSE.ROTATE,
                MIDDLE: THREE.MOUSE.PAN,
                RIGHT: THREE.MOUSE.PAN
            };

            controls.update();

            // Lighting
            this._setupLighting(scene);

            // Load saved positions from localStorage and apply to config
            const savedPositions = this._loadWorkPositions(elementId);
            if (savedPositions && Object.keys(savedPositions).length > 0) {
                console.log(`✓ Loaded ${Object.keys(savedPositions).length} saved work positions from localStorage`);
                config.works.forEach(work => {
                    if (savedPositions[work.id]) {
                        work.posX = savedPositions[work.id].x;
                        work.posZ = savedPositions[work.id].z;
                        console.log(`  Applied saved position for ${work.id}: (${work.posX}, ${work.posZ})`);
                    }
                });
            } else {
                console.log('No saved positions found, using default layout');
            }

            // Medium Priority Fix 3.4: Handle empty works array
            const works = config.works || [];
            const flowZones = config.flowZones || [];
            const connections = config.connections || [];
            const floorSize = Math.max(config.floorSize || 100, 100);

            // Update fog and camera max distance for large floors
            scene.fog = new THREE.Fog(0x0f172a, floorSize, floorSize * 3);
            controls.maxDistance = Math.max(300, floorSize * 3);

            let stationMeshes = {};

            if (works.length === 0 && flowZones.length > 0) {
                // Device scene mode: Create floor and flow zones only (devices will be added via addDeviceAtPosition)
                console.log(`Device scene mode: Creating floor (size: ${floorSize}) and ${flowZones.length} flow zones...`);
                this._createDeviceSceneFloor(scene, flowZones, floorSize);

                // Set camera position for device view
                camera.position.set(0, 15, 35);
                camera.lookAt(0, 0, 0);
                controls.update();
            } else if (works.length === 0) {
                // Empty scene
                console.warn(`No works or flow zones provided for scene '${elementId}'`);
                scene.add(new THREE.GridHelper(20, 20, 0x6b7280, 0x52596380));
                camera.position.set(0, 10, 15);
                camera.lookAt(0, 0, 0);
                controls.update();
            } else {
                // Work scene mode: Create factory floor, flow zones, and stations
                stationMeshes = this._createFactoryScene(scene, works, flowZones, floorSize);
            }

            // Create work connections (arrows) and store references
            const workConnectionLines = this._createConnections(scene, connections, stationMeshes);

            // Auto-scale camera to fit all works (only if works exist)
            if (works.length > 0) {
                this._fitCameraToScene(scene, camera, controls, works);
            }

            // Medium Priority Fix 3.1: Animation loop with error handling
            let animationId;
            let time = 0;
            const animate = () => {
                try {
                    animationId = requestAnimationFrame(animate);
                    time += 0.016; // ~60fps

                    // Animate AAS icons (floating effect)
                    scene.traverse((obj) => {
                        if (obj.userData && obj.userData.isAASIcon) {
                            const floatOffset = obj.userData.floatOffset || 0;
                            const baseY = obj.userData.baseY || 5.5; // Base position from userData
                            const amplitude = 0.3; // How much it moves up and down
                            const frequency = 1.5; // Speed of floating

                            obj.position.y = baseY + Math.sin(time * frequency + floatOffset) * amplitude;
                        }

                        // Animate robots when state is Going ('G')
                        // Check both workData (for Work-based entities) and deviceData (for Device-based entities)
                        const robotState = obj.userData?.workData?.state || obj.userData?.deviceData?.state;
                        if (obj.userData && obj.userData.isRobot && robotState === 'G') {

                            // Initialize welding particles if not exists
                            if (!obj.userData.weldParticles) {
                                obj.userData.weldParticles = this._createWeldingParticles();
                                obj.add(obj.userData.weldParticles);
                            }

                            // Animate robot parts
                            let gripperObj = null;
                            obj.traverse((child) => {
                                if (child.name === 'robotJ1') {
                                    // Base rotation - slow continuous spin
                                    child.rotation.y = Math.sin(time * 0.5) * 0.3; // ±17° oscillation
                                }
                                else if (child.name === 'robotTower') {
                                    // Body tower rotation - synchronized with base
                                    child.rotation.y = Math.sin(time * 0.5 + 0.2) * 0.15; // ±8.6° oscillation, slight phase offset
                                }
                                else if (child.name === 'robotUpperArm') {
                                    // Upper arm - up/down motion (more pronounced)
                                    const baseRot = child.userData.baseRotationZ || 0;
                                    child.rotation.z = baseRot + Math.sin(time * 1.2) * 0.25; // ±14.3° oscillation (increased)
                                }
                                else if (child.name === 'robotForearm') {
                                    // Forearm - up/down motion (opposite phase, more pronounced)
                                    const baseRot = child.userData.baseRotationZ || 0;
                                    child.rotation.z = baseRot + Math.sin(time * 1.2 + Math.PI) * 0.3; // ±17.2° oscillation (increased)
                                }
                                else if (child.name === 'robotLeftFinger') {
                                    // Left gripper - open/close motion
                                    const basePos = child.userData.basePositionZ || 0;
                                    child.position.z = basePos + Math.sin(time * 2.5) * 0.08; // Increased motion
                                }
                                else if (child.name === 'robotRightFinger') {
                                    // Right gripper - open/close motion (opposite)
                                    const basePos = child.userData.basePositionZ || 0;
                                    child.position.z = basePos - Math.sin(time * 2.5) * 0.08; // Increased motion
                                }
                                else if (child.name === 'robotGripper') {
                                    // Store gripper object reference
                                    gripperObj = child;
                                }
                            });

                            // Update welding particles
                            if (obj.userData.weldParticles && gripperObj) {
                                this._updateWeldingParticles(obj.userData.weldParticles, gripperObj, obj, time);
                            }
                        }
                        // Reset robot parts and remove particles when NOT Going
                        else if (obj.userData && obj.userData.isRobot && robotState && robotState !== 'G') {

                            // Remove welding particles
                            if (obj.userData.weldParticles) {
                                obj.remove(obj.userData.weldParticles);
                                obj.userData.weldParticles.geometry.dispose();
                                obj.userData.weldParticles.material.dispose();
                                obj.userData.weldParticles = null;
                            }

                            obj.traverse((child) => {
                                if (child.name === 'robotJ1') {
                                    child.rotation.y = 0;
                                }
                                else if (child.name === 'robotTower') {
                                    child.rotation.y = 0;
                                }
                                else if (child.name === 'robotUpperArm') {
                                    child.rotation.z = child.userData.baseRotationZ || 0;
                                }
                                else if (child.name === 'robotForearm') {
                                    child.rotation.z = child.userData.baseRotationZ || 0;
                                }
                                else if (child.name === 'robotLeftFinger') {
                                    child.position.z = child.userData.basePositionZ || 0;
                                }
                                else if (child.name === 'robotRightFinger') {
                                    child.position.z = child.userData.basePositionZ || 0;
                                }
                            });
                        }

                        // Animate ApiDef arrow flow particles
                        if (obj.userData && obj.userData.isFlowParticle) {
                            const points = obj.userData.curvePoints;
                            if (points && points.length > 0) {
                                // Update progress
                                obj.userData.progress += (obj.userData.reversed ? -0.005 : 0.005);
                                if (obj.userData.progress > 1) obj.userData.progress = 0;
                                if (obj.userData.progress < 0) obj.userData.progress = 1;

                                // Get position along curve
                                const index = Math.floor(obj.userData.progress * (points.length - 1));
                                obj.position.copy(points[index]);
                            }
                        }

                        // Lib3D 모션 애니메이션 (activeAnimation이 설정된 Device Indicator)
                        if (obj.userData && obj.userData.isDeviceIndicator && obj.userData.activeAnimation) {
                            if (window.Ds2View3DLibrary) {
                                window.Ds2View3DLibrary.animate(obj, obj.userData.activeAnimation, 0.08);
                            }
                        }
                    });

                    controls.update();
                    renderer.render(scene, camera);
                } catch (error) {
                    console.error(`Animation error for scene '${elementId}':`, error);
                    // Continue animation despite error
                    animationId = requestAnimationFrame(animate);
                }
            };
            animate();

            // Medium Priority Fix 3.3: Collect call cubes for caching
            const callCubes = new Map();
            scene.traverse((obj) => {
                if (obj.userData && obj.userData.callId) {
                    callCubes.set(obj.userData.callId, obj);
                }
            });
            console.log(`✓ Cached ${callCubes.size} call cubes for fast lookup`);

            // Store scene data
            this._scenes[elementId] = {
                scene,
                camera,
                renderer,
                controls,
                stationMeshes,
                deviceMeshes: {},             // For device-based drag-drop
                devices: [],                  // For device-based drag-drop
                container,
                animationId,
                config: config,
                selectedObject: null,
                selectedCall: null,           // Currently selected call ID for chain visualization
                hoveredObject: null,
                connectionLines: [],
                highlightedObjects: [],
                workConnectionLines: workConnectionLines,  // Work 간 연결 화살표
                callSelectionCallback: null,  // Blazor callback for call selection
                editMode: false,  // Device edit mode (drag & drop)
                // Medium Priority Fix 3.2: Reusable objects for performance
                reusableVector2: new THREE.Vector2(),
                reusableVector3: new THREE.Vector3(),
                reusableRaycaster: new THREE.Raycaster(),
                // Medium Priority Fix 3.3: Call cubes cache for faster lookups
                callCubes: callCubes,
                // Call chain visualization state
                callChainLines: [],
                // Flow Zone auto-recalculation debounce timer
                flowRecalcTimer: null,
                callChainHighlights: []
            };

            // Setup interaction handlers
            this._setupInteractionHandlers(elementId);

            // Handle window resize
            const resizeHandler = () => this._handleResize(elementId);
            window.addEventListener('resize', resizeHandler);
            this._scenes[elementId].resizeHandler = resizeHandler;

            console.log(`3D scene '${elementId}' initialized with ${Object.keys(stationMeshes).length} stations`);
            return true;

        } catch (error) {
            console.error('Failed to initialize 3D scene:', error);
            return false;
        }
    },

    /**
     * Update work states (triggers color changes)
     * @param {string} elementId - Scene container id
     * @param {array} workStates - Array of {id, state} objects
     */
    updateWorkStates: function(elementId, workStates) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn(`Scene '${elementId}' not found for state update`);
            return;
        }

        if (!Array.isArray(workStates)) {
            console.warn('workStates must be an array');
            return;
        }

        workStates.forEach(work => {
            const stationMesh = sceneData.stationMeshes[work.id];
            if (stationMesh) {
                const colorConfig = this.stateColors[work.state] || this.stateColors.R;

                // Update workData state for animation (CRITICAL for robot animation)
                if (stationMesh.userData && stationMesh.userData.workData) {
                    stationMesh.userData.workData.state = work.state;
                }

                // Update main device model color (traverse all materials)
                stationMesh.traverse(child => {
                    if (child.isMesh && child.material) {
                        // Only update materials that have emissive property (body parts, not joints/base)
                        if (child.material.emissive && child.material.emissiveIntensity > 0.2) {
                            child.material.emissive.setHex(colorConfig.hex);
                            child.material.emissiveIntensity = work.state === 'G' ? 0.5 : 0.3;
                        }
                    }
                    // Also update workData state in all children
                    if (child.userData && child.userData.workData) {
                        child.userData.workData.state = work.state;
                    }
                });

                console.log(`Updated work ${work.id} state to ${work.state} (animation enabled: ${stationMesh.userData?.isRobot})`);
            }
        });
    },

    /**
     * Create welding particle system
     * @returns {THREE.Points} Particle system
     */
    _createWeldingParticles: function() {
        const particleCount = 100; // Increased from 50 to 100 for more spectacular effect
        const geometry = new THREE.BufferGeometry();
        const positions = new Float32Array(particleCount * 3);
        const velocities = new Float32Array(particleCount * 3);
        const lifetimes = new Float32Array(particleCount);
        const sizes = new Float32Array(particleCount);

        // Initialize particles
        for (let i = 0; i < particleCount; i++) {
            const i3 = i * 3;
            // Start at origin (will be repositioned in update)
            positions[i3] = 0;
            positions[i3 + 1] = 0;
            positions[i3 + 2] = 0;

            // Random velocities
            velocities[i3] = (Math.random() - 0.5) * 0.5;
            velocities[i3 + 1] = Math.random() * 0.3 + 0.1; // Upward bias
            velocities[i3 + 2] = (Math.random() - 0.5) * 0.5;

            // Random lifetime
            lifetimes[i] = Math.random();

            // Random size
            sizes[i] = Math.random() * 0.15 + 0.05;
        }

        geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geometry.setAttribute('velocity', new THREE.BufferAttribute(velocities, 3));
        geometry.setAttribute('lifetime', new THREE.BufferAttribute(lifetimes, 1));
        geometry.setAttribute('size', new THREE.BufferAttribute(sizes, 1));

        // Welding spark material (bright orange/yellow)
        const material = new THREE.PointsMaterial({
            size: 0.25,
            color: 0xffaa00, // Bright orange-yellow
            transparent: true,
            opacity: 0.95,
            blending: THREE.AdditiveBlending,
            depthWrite: false,
            sizeAttenuation: true
        });

        const particles = new THREE.Points(geometry, material);
        particles.userData.initialized = false;
        return particles;
    },

    /**
     * Update welding particle positions
     * @param {THREE.Points} particles - Particle system
     * @param {THREE.Object3D} gripperObj - Gripper mesh object
     * @param {THREE.Object3D} robotObj - Robot group object
     * @param {number} time - Animation time
     */
    _updateWeldingParticles: function(particles, gripperObj, robotObj, time) {
        const positions = particles.geometry.attributes.position.array;
        const velocities = particles.geometry.attributes.velocity.array;
        const lifetimes = particles.geometry.attributes.lifetime.array;
        const particleCount = lifetimes.length;

        // Get gripper world position
        const gripperWorldPos = new THREE.Vector3();
        gripperObj.getWorldPosition(gripperWorldPos);

        // Convert to robot local space using inverse matrix
        const localGripperPos = robotObj.worldToLocal(gripperWorldPos.clone());

        for (let i = 0; i < particleCount; i++) {
            const i3 = i * 3;

            // Update lifetime
            lifetimes[i] -= 0.025;

            // Respawn particle if dead
            if (lifetimes[i] <= 0) {
                // Spawn exactly at gripper center
                positions[i3] = localGripperPos.x;
                positions[i3 + 1] = localGripperPos.y;
                positions[i3 + 2] = localGripperPos.z;

                // Create radial burst velocity (spherical distribution)
                // Random angles for spherical coordinates
                const theta = Math.random() * Math.PI * 2; // 0 to 2π (horizontal angle)
                const phi = Math.acos(Math.random() * 2 - 1); // 0 to π (vertical angle, uniform distribution)
                const speed = 0.4 + Math.random() * 0.4; // Speed: 0.4 to 0.8

                // Convert spherical to Cartesian coordinates
                velocities[i3] = speed * Math.sin(phi) * Math.cos(theta); // X
                velocities[i3 + 1] = speed * Math.cos(phi); // Y (mainly upward)
                velocities[i3 + 2] = speed * Math.sin(phi) * Math.sin(theta); // Z

                // Add slight upward bias for welding sparks
                velocities[i3 + 1] += 0.2;

                // Reset lifetime
                lifetimes[i] = 1.0;
            } else {
                // Update position based on velocity
                positions[i3] += velocities[i3];
                positions[i3 + 1] += velocities[i3 + 1];
                positions[i3 + 2] += velocities[i3 + 2];

                // Apply gravity
                velocities[i3 + 1] -= 0.015;

                // Add air resistance for more realistic sparks
                velocities[i3] *= 0.98;
                velocities[i3 + 1] *= 0.98;
                velocities[i3 + 2] *= 0.98;
            }
        }

        particles.geometry.attributes.position.needsUpdate = true;
        particles.geometry.attributes.lifetime.needsUpdate = true;

        // Pulse effect for material (more intense)
        const pulseFactor = Math.sin(time * 15) * 0.4 + 0.6;
        particles.material.opacity = 0.7 + pulseFactor * 0.3;
    },

    /**
     * Dispose scene and cleanup resources
     * @param {string} elementId - Scene container id
     */
    dispose: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        try {
            // Stop animation
            if (sceneData.animationId) {
                cancelAnimationFrame(sceneData.animationId);
            }

            // Remove resize listener
            if (sceneData.resizeHandler) {
                window.removeEventListener('resize', sceneData.resizeHandler);
            }

            // Critical Fix 1.3: Remove ALL interaction handlers to prevent memory leaks
            const canvas = sceneData.renderer.domElement;
            if (sceneData.clickHandler) {
                canvas.removeEventListener('click', sceneData.clickHandler);
            }
            if (sceneData.mouseMoveHandler) {
                canvas.removeEventListener('mousemove', sceneData.mouseMoveHandler);
            }
            if (sceneData.mouseDownHandler) {
                canvas.removeEventListener('mousedown', sceneData.mouseDownHandler);
            }
            if (sceneData.mouseUpHandler) {
                canvas.removeEventListener('mouseup', sceneData.mouseUpHandler);
            }

            // Remove detail panel
            this._hideDetailPanel();

            // Dispose controls
            if (sceneData.controls) {
                sceneData.controls.dispose();
            }

            // Dispose Three.js resources
            sceneData.scene.traverse(obj => {
                if (obj.geometry) obj.geometry.dispose();
                if (obj.material) {
                    if (Array.isArray(obj.material)) {
                        obj.material.forEach(mat => {
                            if (mat.map) mat.map.dispose();
                            mat.dispose();
                        });
                    } else {
                        if (obj.material.map) obj.material.map.dispose();
                        obj.material.dispose();
                    }
                }
            });

            // Dispose renderer
            sceneData.renderer.dispose();

            // Remove canvas
            if (sceneData.container && sceneData.renderer.domElement) {
                sceneData.container.removeChild(sceneData.renderer.domElement);
            }

            // Medium Priority Fix 3.3: Clear call cubes cache
            if (sceneData.callCubes) {
                sceneData.callCubes.clear();
            }

            delete this._scenes[elementId];
            console.log(`3D scene '${elementId}' disposed`);

        } catch (error) {
            console.error(`Error disposing scene '${elementId}':`, error);
        }
    },

    /**
     * Critical Fix 1.4: Recursively dispose a Three.js object and its children
     * @param {THREE.Object3D} object - Object to dispose
     */
    _disposeSceneRecursive: function(object) {
        if (!object) return;

        // Dispose geometry
        if (object.geometry) {
            object.geometry.dispose();
        }

        // Dispose material(s)
        if (object.material) {
            if (Array.isArray(object.material)) {
                object.material.forEach(mat => {
                    this._disposeMaterial(mat);
                });
            } else {
                this._disposeMaterial(object.material);
            }
        }

        // Dispose children recursively
        if (object.children && object.children.length > 0) {
            // Copy array to avoid modification during iteration
            const children = [...object.children];
            children.forEach(child => {
                this._disposeSceneRecursive(child);
                object.remove(child);
            });
        }
    },

    /**
     * Critical Fix 1.4: Dispose a Three.js material and its textures
     * @param {THREE.Material} material - Material to dispose
     */
    _disposeMaterial: function(material) {
        if (!material) return;

        // Dispose textures
        if (material.map) material.map.dispose();
        if (material.lightMap) material.lightMap.dispose();
        if (material.bumpMap) material.bumpMap.dispose();
        if (material.normalMap) material.normalMap.dispose();
        if (material.specularMap) material.specularMap.dispose();
        if (material.envMap) material.envMap.dispose();

        material.dispose();
    },


    /**
     * Enable/disable device edit mode
     * @param {string} elementId - Scene identifier
     * @param {boolean} enabled - true = edit mode ON, false = camera mode ON
     */
    setEditMode: function(elementId, enabled) {
        try {
            console.log(`[EditMode] Called with elementId='${elementId}', enabled=${enabled}`);

            const sceneData = this._scenes[elementId];
            if (!sceneData) {
                console.error('[EditMode] Scene not found:', elementId);
                console.error('[EditMode] Available scenes:', Object.keys(this._scenes));
                return;
            }

            console.log('[EditMode] Scene found, updating state...');

            // Update edit mode state
            sceneData.editMode = enabled;

            // CRITICAL: Synchronize OrbitControls (inverse relationship)
            if (sceneData.controls) {
                sceneData.controls.enabled = !enabled;
                console.log(`[EditMode] ✓ OrbitControls.enabled = ${!enabled}`);
            } else {
                console.warn('[EditMode] ⚠️ No controls found!');
            }

            // Update cursor
            if (sceneData.renderer && sceneData.renderer.domElement) {
                sceneData.renderer.domElement.style.cursor = enabled ? 'grab' : 'default';
                console.log(`[EditMode] ✓ Cursor = ${enabled ? 'grab' : 'default'}`);
            }

            console.log(`[EditMode] ✓ Edit mode: ${enabled ? 'ON (device drag)' : 'OFF (camera control)'}`);
        } catch (error) {
            console.error('[EditMode] ERROR:', error);
            console.error('[EditMode] Stack:', error.stack);
        }
    },

    /**
     * Toggle grid snapping for device placement
     * @param {string} elementId - Scene identifier
     * @param {boolean} enabled - true = snap to grid, false = free placement
     */
    setGridSnap: function(elementId, enabled) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;
        sceneData.gridSnap = enabled;
        console.log(`[GridSnap] ${enabled ? 'ON' : 'OFF'} (gridSize=5)`);
    },

    /**
     * Register Blazor callback for call selection
     * @param {string} elementId - Scene identifier
     * @param {object} dotNetRef - DotNetObjectReference for callback
     */
    setCallSelectionCallback: function(elementId, dotNetRef) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.error(`Scene '${elementId}' not found`);
            return;
        }

        sceneData.callSelectionCallback = dotNetRef;
        console.log(`✓ Registered call selection callback for scene '${elementId}'`);
    },

    /**
     * Shuffle work/device layout with smooth animation
     * @param {string} elementId - Scene identifier
     */
    shuffleLayout: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.error(`Scene '${elementId}' not found`);
            return;
        }

        console.log(`Shuffling layout for scene '${elementId}'`);

        // Clear all visualizations before shuffling
        this._clearCallChainVisualization(elementId);
        this._clearHighlights(elementId);
        sceneData.selectedObject = null;
        sceneData.selectedCall = null;
        console.log('✓ Cleared visualizations for shuffle');

        // Get all station groups (Works)
        const stations = [];
        if (sceneData.stationMeshes) {
            Object.keys(sceneData.stationMeshes).forEach(workId => {
                const mesh = sceneData.stationMeshes[workId];
                const group = mesh.parent;
                if (group) {
                    stations.push({
                        id: workId,
                        type: 'work',
                        group: group,
                        originalPos: {
                            x: group.position.x,
                            y: group.position.y,
                            z: group.position.z
                        }
                    });
                }
            });
        }

        // Get all device groups (Devices)
        if (sceneData.deviceMeshes) {
            Object.keys(sceneData.deviceMeshes).forEach(deviceId => {
                const group = sceneData.deviceMeshes[deviceId];  // deviceMeshes now stores groups directly
                if (group) {
                    stations.push({
                        id: deviceId,
                        type: 'device',
                        group: group,
                        originalPos: {
                            x: group.position.x,
                            y: group.position.y,
                            z: group.position.z
                        }
                    });
                }
            });
        }

        if (stations.length === 0) {
            console.warn('No stations or devices found to shuffle');
            return;
        }

        console.log(`Shuffling ${stations.length} items (works + devices)`);

        // Calculate bounding area from original positions
        const minX = Math.min(...stations.map(s => s.originalPos.x)) - 5;
        const maxX = Math.max(...stations.map(s => s.originalPos.x)) + 5;
        const minZ = Math.min(...stations.map(s => s.originalPos.z)) - 5;
        const maxZ = Math.max(...stations.map(s => s.originalPos.z)) + 5;

        // Generate random target positions within the area
        stations.forEach(station => {
            station.targetPos = {
                x: minX + Math.random() * (maxX - minX),
                y: station.originalPos.y,
                z: minZ + Math.random() * (maxZ - minZ)
            };
        });

        // Animate to new positions
        const duration = 1500; // 1.5 seconds
        const startTime = Date.now();

        const animateShuffle = () => {
            const elapsed = Date.now() - startTime;
            const progress = Math.min(elapsed / duration, 1.0);

            // Ease out cubic for smooth deceleration
            const eased = 1 - Math.pow(1 - progress, 3);

            stations.forEach(station => {
                station.group.position.x = station.originalPos.x +
                    (station.targetPos.x - station.originalPos.x) * eased;
                station.group.position.z = station.originalPos.z +
                    (station.targetPos.z - station.originalPos.z) * eased;

                // Add bounce effect at the end
                if (progress > 0.8) {
                    const bounceProgress = (progress - 0.8) / 0.2;
                    station.group.position.y = station.originalPos.y +
                        Math.sin(bounceProgress * Math.PI) * 2;
                }
            });

            if (progress < 1.0) {
                requestAnimationFrame(animateShuffle);
            } else {
                // Finalize positions
                stations.forEach(station => {
                    station.group.position.y = station.originalPos.y;
                });
                console.log('Shuffle animation complete');

                // Save new positions to localStorage (both works and devices)
                this._saveWorkPositions(elementId);
                this._saveDevicePositions(elementId, this._collectDevicePositions(sceneData));
            }
        };

        animateShuffle();
    },

    /**
     * Collect current device positions from scene
     */
    _collectDevicePositions: function(sceneData) {
        const positions = {};
        if (sceneData.devices) {
            sceneData.devices.forEach(d => {
                // Get current position from the group
                const mesh = sceneData.deviceMeshes?.[d.id];
                if (mesh && mesh.parent) {
                    positions[d.id] = {
                        x: mesh.parent.position.x,
                        z: mesh.parent.position.z
                    };
                } else {
                    positions[d.id] = { x: d.posX, z: d.posZ };
                }
            });
        }
        return positions;
    },

    /**
     * Reset layout to default (clear saved positions and reload page)
     * @param {string} elementId - Scene identifier
     */
    resetLayout: function(elementId) {
        console.log(`Resetting layout for scene '${elementId}'`);

        // Clear all arrows from the scene
        this._clearCallChainVisualization(elementId);
        this._clearHighlights(elementId);

        const sceneData = this._scenes[elementId];
        if (sceneData) {
            sceneData.selectedObject = null;
            sceneData.selectedCall = null;
        }
        console.log('✓ All arrows cleared');

        this._clearSavedPositions(elementId);
        console.log('✓ Layout reset complete');
    },

    /**
     * Classify device type from device name
     * @param {string} deviceName - Device name to classify
     * @returns {string} Device type: 'robot', 'small', or 'general'
     */
    _classifyDeviceType: function(deviceName) {
        // High Priority Fix 2.3: Enhanced type safety
        if (!deviceName) {
            return 'general';
        }

        // Ensure string type
        const nameStr = String(deviceName);
        if (typeof nameStr !== 'string' || nameStr.trim() === '') {
            return 'general';
        }

        const name = nameStr.toUpperCase();

        // Robot keywords
        const robotKeywords = ['RB', 'RBT', 'ROBOT', 'ARM', 'MANIPULATOR', 'COBOT'];
        if (robotKeywords.some(keyword => name.includes(keyword))) {
            return 'robot';
        }

        // Small device keywords
        const smallDeviceKeywords = ['CLP', 'CLAMP', 'LATCH', 'PIN', 'GRIP', 'CHUCK', 'VALVE', 'SENSOR'];
        if (smallDeviceKeywords.some(keyword => name.includes(keyword))) {
            return 'small';
        }

        // Default: general
        return 'general';
    },

    /**
     * Get representative device type for a work station
     * @param {object} work - Work object with calls
     * @returns {string} Device type ('robot', 'small', or 'general')
     */
    _getWorkDeviceType: function(work) {
        if (!work.calls || work.calls.length === 0) {
            return 'general';
        }

        // Count device types across all calls
        const typeCounts = { robot: 0, small: 0, general: 0 };

        work.calls.forEach(call => {
            const type = this._classifyDeviceType(call.device);
            typeCounts[type]++;
        });

        // Return the most common device type
        if (typeCounts.robot > 0) return 'robot';
        if (typeCounts.small > 0) return 'small';
        return 'general';
    },

    /**
     * Create 3D model for robot-type device (Industrial 6-axis robot style)
     * @param {number} size - Base size for the model
     * @param {object} stateColor - State color object
     * @returns {THREE.Group} Detailed robot model group
     */
    _createRobotModel: function(size, stateColor) {
        const group = new THREE.Group();

        // Material definitions
        const baseMetal = new THREE.MeshStandardMaterial({
            color: 0x2d3748,
            metalness: 0.8,
            roughness: 0.3
        });
        const bodyMetal = new THREE.MeshStandardMaterial({
            color: stateColor.hex,
            emissive: stateColor.hex,
            emissiveIntensity: 0.3,
            metalness: 0.7,
            roughness: 0.2
        });
        const jointMetal = new THREE.MeshStandardMaterial({
            color: 0x475569,
            metalness: 0.9,
            roughness: 0.1
        });
        const gripperMetal = new THREE.MeshStandardMaterial({
            color: 0xf59e0b,
            emissive: 0xf59e0b,
            emissiveIntensity: 0.4,
            metalness: 0.6,
            roughness: 0.3
        });

        // === BASE PLATFORM (J0) ===
        // Circular base with mounting plate
        const basePlateGeo = new THREE.CylinderGeometry(size * 1.0, size * 1.1, size * 0.3, 32);
        const basePlate = new THREE.Mesh(basePlateGeo, baseMetal);
        basePlate.position.y = size * 0.15;
        group.add(basePlate);

        // Mounting bolts (8 around perimeter)
        for (let i = 0; i < 8; i++) {
            const angle = (Math.PI * 2 / 8) * i;
            const boltGeo = new THREE.CylinderGeometry(size * 0.08, size * 0.08, size * 0.25, 8);
            const boltMat = new THREE.MeshStandardMaterial({ color: 0x1e293b, metalness: 0.9, roughness: 0.2 });
            const bolt = new THREE.Mesh(boltGeo, boltMat);
            bolt.position.set(
                Math.cos(angle) * size * 0.85,
                size * 0.05,
                Math.sin(angle) * size * 0.85
            );
            group.add(bolt);
        }

        // === J1 ROTARY JOINT (Base rotation) ===
        const j1Geo = new THREE.CylinderGeometry(size * 0.7, size * 0.8, size * 0.5, 24);
        const j1 = new THREE.Mesh(j1Geo, jointMetal);
        j1.position.y = size * 0.55;
        j1.name = 'robotJ1'; // For animation
        group.add(j1);

        // === MAIN BODY TOWER ===
        const towerGeo = new THREE.CylinderGeometry(size * 0.6, size * 0.65, size * 1.0, 20);
        const tower = new THREE.Mesh(towerGeo, bodyMetal);
        tower.position.y = size * 1.3;
        tower.name = 'robotTower'; // For animation
        group.add(tower);

        // Cable/hose details on tower
        const cableGeo = new THREE.CylinderGeometry(size * 0.08, size * 0.08, size * 0.9, 8);
        const cableMat = new THREE.MeshStandardMaterial({ color: 0x1a202c, metalness: 0.3, roughness: 0.7 });
        const cable = new THREE.Mesh(cableGeo, cableMat);
        cable.position.set(size * 0.5, size * 1.3, 0);
        group.add(cable);

        // === J2 SHOULDER JOINT ===
        const j2Geo = new THREE.SphereGeometry(size * 0.45, 16, 16);
        const j2 = new THREE.Mesh(j2Geo, jointMetal);
        j2.position.y = size * 1.8;
        group.add(j2);

        // === UPPER ARM (Segment 1) ===
        const upperArmGeo = new THREE.BoxGeometry(size * 1.4, size * 0.35, size * 0.35);
        const upperArm = new THREE.Mesh(upperArmGeo, bodyMetal);
        upperArm.position.set(size * 0.7, size * 2.05, 0);
        upperArm.rotation.z = Math.PI / 7; // 25.7° angle
        upperArm.name = 'robotUpperArm'; // For animation
        upperArm.userData.baseRotationZ = Math.PI / 7; // Store original rotation
        group.add(upperArm);

        // Upper arm details (reinforcement ribs)
        for (let i = 0; i < 3; i++) {
            const ribGeo = new THREE.BoxGeometry(size * 0.1, size * 0.4, size * 0.4);
            const rib = new THREE.Mesh(ribGeo, jointMetal);
            rib.position.set(size * (0.2 + i * 0.4), size * 2.05, 0);
            rib.rotation.z = Math.PI / 7;
            group.add(rib);
        }

        // === J3 ELBOW JOINT ===
        const j3Geo = new THREE.SphereGeometry(size * 0.35, 16, 16);
        const j3 = new THREE.Mesh(j3Geo, jointMetal);
        j3.position.set(size * 1.35, size * 2.35, 0);
        group.add(j3);

        // === FOREARM (Segment 2) ===
        const forearmGeo = new THREE.CylinderGeometry(size * 0.25, size * 0.28, size * 1.2, 16);
        const forearm = new THREE.Mesh(forearmGeo, bodyMetal);
        forearm.position.set(size * 1.75, size * 2.2, 0);
        forearm.rotation.z = -Math.PI / 9; // -20° angle
        forearm.name = 'robotForearm'; // For animation
        forearm.userData.baseRotationZ = -Math.PI / 9; // Store original rotation
        group.add(forearm);

        // === WRIST ASSEMBLY (J4, J5, J6) ===
        const wristBaseGeo = new THREE.CylinderGeometry(size * 0.22, size * 0.25, size * 0.3, 12);
        const wristBase = new THREE.Mesh(wristBaseGeo, jointMetal);
        wristBase.position.set(size * 2.1, size * 1.8, 0);
        wristBase.rotation.z = -Math.PI / 9;
        group.add(wristBase);

        const wristMidGeo = new THREE.CylinderGeometry(size * 0.18, size * 0.18, size * 0.25, 12);
        const wristMid = new THREE.Mesh(wristMidGeo, jointMetal);
        wristMid.position.set(size * 2.15, size * 1.6, 0);
        wristMid.rotation.x = Math.PI / 2;
        group.add(wristMid);

        // === END EFFECTOR (Gripper with 2 fingers) ===
        // Gripper base
        const gripperBaseGeo = new THREE.BoxGeometry(size * 0.3, size * 0.25, size * 0.25);
        const gripperBase = new THREE.Mesh(gripperBaseGeo, gripperMetal);
        gripperBase.position.set(size * 2.2, size * 1.45, 0);
        gripperBase.name = 'robotGripper'; // For particle spawn point
        group.add(gripperBase);

        // Left finger
        const fingerGeo = new THREE.BoxGeometry(size * 0.15, size * 0.4, size * 0.12);
        const leftFinger = new THREE.Mesh(fingerGeo, gripperMetal);
        leftFinger.position.set(size * 2.2, size * 1.2, size * 0.15);
        leftFinger.name = 'robotLeftFinger'; // For animation
        leftFinger.userData.basePositionZ = size * 0.15; // Store original position
        group.add(leftFinger);

        // Right finger
        const rightFinger = new THREE.Mesh(fingerGeo, gripperMetal);
        rightFinger.position.set(size * 2.2, size * 1.2, -size * 0.15);
        rightFinger.name = 'robotRightFinger'; // For animation
        rightFinger.userData.basePositionZ = -size * 0.15; // Store original position
        group.add(rightFinger);

        // Gripper pads (friction pads on inside of fingers)
        const padGeo = new THREE.BoxGeometry(size * 0.12, size * 0.25, size * 0.04);
        const padMat = new THREE.MeshStandardMaterial({ color: 0x1f2937, roughness: 0.9, metalness: 0.1 });
        const leftPad = new THREE.Mesh(padGeo, padMat);
        leftPad.position.set(size * 2.2, size * 1.2, size * 0.11);
        group.add(leftPad);
        const rightPad = new THREE.Mesh(padGeo, padMat);
        rightPad.position.set(size * 2.2, size * 1.2, -size * 0.11);
        group.add(rightPad);

        // Status LED on wrist
        const ledGeo = new THREE.SphereGeometry(size * 0.1, 8, 8);
        const ledMat = new THREE.MeshStandardMaterial({
            color: 0x10b981,
            emissive: 0x10b981,
            emissiveIntensity: 0.8
        });
        const led = new THREE.Mesh(ledGeo, ledMat);
        led.position.set(size * 2.05, size * 1.85, size * 0.25);
        group.add(led);

        // Mark this group as a robot for animation
        group.userData.isRobot = true;

        return group;
    },

    /**
     * Create 3D model for small device (Pneumatic gripper/clamp style)
     * @param {number} size - Base size for the model
     * @param {object} stateColor - State color object
     * @returns {THREE.Group} Detailed small device model group
     */
    _createSmallDeviceModel: function(size, stateColor) {
        const group = new THREE.Group();

        // Material definitions
        const baseMetal = new THREE.MeshStandardMaterial({
            color: 0x334155,
            metalness: 0.8,
            roughness: 0.25
        });
        const cylinderMetal = new THREE.MeshStandardMaterial({
            color: stateColor.hex,
            emissive: stateColor.hex,
            emissiveIntensity: 0.35,
            metalness: 0.75,
            roughness: 0.2
        });
        const pistonMetal = new THREE.MeshStandardMaterial({
            color: 0x94a3b8,
            metalness: 0.85,
            roughness: 0.15
        });
        const gripMetal = new THREE.MeshStandardMaterial({
            color: 0x64748b,
            metalness: 0.7,
            roughness: 0.3
        });

        // === MOUNTING BASE PLATE ===
        const basePlateGeo = new THREE.BoxGeometry(size * 0.9, size * 0.15, size * 0.7);
        const basePlate = new THREE.Mesh(basePlateGeo, baseMetal);
        basePlate.position.y = size * 0.075;
        group.add(basePlate);

        // Mounting holes (4 corners)
        for (let x = -1; x <= 1; x += 2) {
            for (let z = -1; z <= 1; z += 2) {
                const holeGeo = new THREE.CylinderGeometry(size * 0.06, size * 0.06, size * 0.16, 8);
                const holeMat = new THREE.MeshStandardMaterial({ color: 0x0f172a, metalness: 0.9, roughness: 0.1 });
                const hole = new THREE.Mesh(holeGeo, holeMat);
                hole.position.set(x * size * 0.35, size * 0.075, z * size * 0.25);
                group.add(hole);
            }
        }

        // === PNEUMATIC CYLINDER BODY ===
        const cylinderBodyGeo = new THREE.CylinderGeometry(size * 0.35, size * 0.35, size * 0.8, 20);
        const cylinderBody = new THREE.Mesh(cylinderBodyGeo, cylinderMetal);
        cylinderBody.position.y = size * 0.55;
        group.add(cylinderBody);

        // Cylinder end caps
        const endCapGeo = new THREE.CylinderGeometry(size * 0.38, size * 0.38, size * 0.12, 20);
        const endCap1 = new THREE.Mesh(endCapGeo, baseMetal);
        endCap1.position.y = size * 0.16;
        group.add(endCap1);
        const endCap2 = new THREE.Mesh(endCapGeo, baseMetal);
        endCap2.position.y = size * 0.94;
        group.add(endCap2);

        // Cylinder grooves (detail lines)
        for (let i = 0; i < 4; i++) {
            const grooveGeo = new THREE.TorusGeometry(size * 0.36, size * 0.02, 8, 20);
            const grooveMat = new THREE.MeshStandardMaterial({ color: 0x1e293b, metalness: 0.5, roughness: 0.6 });
            const groove = new THREE.Mesh(grooveGeo, grooveMat);
            groove.position.y = size * (0.25 + i * 0.2);
            groove.rotation.x = Math.PI / 2;
            group.add(groove);
        }

        // === PISTON ROD ===
        const pistonRodGeo = new THREE.CylinderGeometry(size * 0.12, size * 0.12, size * 0.5, 12);
        const pistonRod = new THREE.Mesh(pistonRodGeo, pistonMetal);
        pistonRod.position.y = size * 1.19;
        group.add(pistonRod);

        // Piston rod cap (connection point)
        const rodCapGeo = new THREE.CylinderGeometry(size * 0.18, size * 0.15, size * 0.15, 12);
        const rodCap = new THREE.Mesh(rodCapGeo, pistonMetal);
        rodCap.position.y = size * 1.44;
        group.add(rodCap);

        // === GRIPPER ARMS (2 symmetric jaws) ===
        // Left arm base
        const armBaseGeo = new THREE.BoxGeometry(size * 0.15, size * 0.25, size * 0.2);
        const leftArmBase = new THREE.Mesh(armBaseGeo, gripMetal);
        leftArmBase.position.set(-size * 0.2, size * 1.44, 0);
        group.add(leftArmBase);

        // Left arm finger
        const fingerGeo = new THREE.BoxGeometry(size * 0.12, size * 0.45, size * 0.18);
        const leftFinger = new THREE.Mesh(fingerGeo, gripMetal);
        leftFinger.position.set(-size * 0.25, size * 1.21, 0);
        leftFinger.rotation.z = Math.PI / 12; // 15° inward angle
        group.add(leftFinger);

        // Right arm base
        const rightArmBase = new THREE.Mesh(armBaseGeo, gripMetal);
        rightArmBase.position.set(size * 0.2, size * 1.44, 0);
        group.add(rightArmBase);

        // Right arm finger
        const rightFinger = new THREE.Mesh(fingerGeo, gripMetal);
        rightFinger.position.set(size * 0.25, size * 1.21, 0);
        rightFinger.rotation.z = -Math.PI / 12; // 15° inward angle
        group.add(rightFinger);

        // Grip pads (rubber-like material on inside of jaws)
        const gripPadGeo = new THREE.BoxGeometry(size * 0.08, size * 0.35, size * 0.15);
        const gripPadMat = new THREE.MeshStandardMaterial({
            color: 0x1f2937,
            roughness: 0.95,
            metalness: 0.05
        });
        const leftPad = new THREE.Mesh(gripPadGeo, gripPadMat);
        leftPad.position.set(-size * 0.21, size * 1.21, 0);
        leftPad.rotation.z = Math.PI / 12;
        group.add(leftPad);
        const rightPad = new THREE.Mesh(gripPadGeo, gripPadMat);
        rightPad.position.set(size * 0.21, size * 1.21, 0);
        rightPad.rotation.z = -Math.PI / 12;
        group.add(rightPad);

        // === AIR HOSE CONNECTION ===
        const hoseConnectorGeo = new THREE.CylinderGeometry(size * 0.08, size * 0.1, size * 0.2, 8);
        const hoseConnector = new THREE.Mesh(hoseConnectorGeo, baseMetal);
        hoseConnector.position.set(size * 0.4, size * 0.35, 0);
        hoseConnector.rotation.z = Math.PI / 2;
        group.add(hoseConnector);

        // Hose
        const hoseGeo = new THREE.CylinderGeometry(size * 0.06, size * 0.06, size * 0.25, 8);
        const hoseMat = new THREE.MeshStandardMaterial({
            color: 0x1a1a1a,
            roughness: 0.8,
            metalness: 0.1
        });
        const hose = new THREE.Mesh(hoseGeo, hoseMat);
        hose.position.set(size * 0.525, size * 0.35, 0);
        hose.rotation.z = Math.PI / 2;
        group.add(hose);

        // === STATUS INDICATOR LIGHTS (2 LEDs) ===
        // Green LED (open state)
        const greenLedGeo = new THREE.CylinderGeometry(size * 0.08, size * 0.08, size * 0.05, 12);
        const greenLedMat = new THREE.MeshStandardMaterial({
            color: 0x10b981,
            emissive: 0x10b981,
            emissiveIntensity: 0.7,
            transparent: true,
            opacity: 0.9
        });
        const greenLed = new THREE.Mesh(greenLedGeo, greenLedMat);
        greenLed.position.set(0, size * 0.98, size * 0.38);
        greenLed.rotation.x = Math.PI / 2;
        group.add(greenLed);

        // Red LED (closed state)
        const redLedGeo = new THREE.CylinderGeometry(size * 0.08, size * 0.08, size * 0.05, 12);
        const redLedMat = new THREE.MeshStandardMaterial({
            color: 0xef4444,
            emissive: 0xef4444,
            emissiveIntensity: 0.3,
            transparent: true,
            opacity: 0.9
        });
        const redLed = new THREE.Mesh(redLedGeo, redLedMat);
        redLed.position.set(0, size * 0.78, size * 0.38);
        redLed.rotation.x = Math.PI / 2;
        group.add(redLed);

        // === SENSOR MODULE (proximity sensor) ===
        const sensorGeo = new THREE.BoxGeometry(size * 0.15, size * 0.12, size * 0.1);
        const sensorMat = new THREE.MeshStandardMaterial({
            color: 0x0ea5e9,
            emissive: 0x0ea5e9,
            emissiveIntensity: 0.4,
            metalness: 0.5,
            roughness: 0.3
        });
        const sensor = new THREE.Mesh(sensorGeo, sensorMat);
        sensor.position.set(0, size * 1.05, -size * 0.38);
        group.add(sensor);

        // Sensor lens
        const lensGeo = new THREE.CircleGeometry(size * 0.05, 16);
        const lensMat = new THREE.MeshStandardMaterial({
            color: 0x1e3a8a,
            emissive: 0x3b82f6,
            emissiveIntensity: 0.8,
            transparent: true,
            opacity: 0.7
        });
        const lens = new THREE.Mesh(lensGeo, lensMat);
        lens.position.set(0, size * 1.05, -size * 0.33);
        group.add(lens);

        return group;
    },

    /**
     * Create 3D model for general device
     * @param {number} size - Base size for the model
     * @param {object} stateColor - State color object
     * @returns {THREE.Mesh} General device cube
     */
    _createGeneralDeviceModel: function(size, stateColor) {
        // Standard cube (existing design)
        const geo = new THREE.BoxGeometry(size, size, size);
        const mat = new THREE.MeshStandardMaterial({
            color: stateColor.hex,
            emissive: stateColor.hex,
            emissiveIntensity: 0.4,
            metalness: 0.5,
            roughness: 0.3
        });
        return new THREE.Mesh(geo, mat);
    },

    // Private helper methods
    _setupLighting: function(scene) {
        // Brighter ambient light for factory environment
        const ambient = new THREE.AmbientLight(0xffffff, 0.6);
        scene.add(ambient);

        // Main directional light with shadows
        const main = new THREE.DirectionalLight(0xffffff, 1.0);
        main.position.set(10, 20, 10);
        main.castShadow = true;
        main.shadow.mapSize.width = 2048;
        main.shadow.mapSize.height = 2048;
        main.shadow.camera.left = -30;
        main.shadow.camera.right = 30;
        main.shadow.camera.top = 30;
        main.shadow.camera.bottom = -30;
        scene.add(main);

        // Fill light from opposite direction
        const fill = new THREE.DirectionalLight(0xb0c4de, 0.4);
        fill.position.set(-10, 10, -10);
        scene.add(fill);

        // Additional overhead lights (like factory ceiling lights)
        const overhead1 = new THREE.PointLight(0xffffff, 0.6, 50);
        overhead1.position.set(15, 15, 15);
        scene.add(overhead1);

        const overhead2 = new THREE.PointLight(0xffffff, 0.6, 50);
        overhead2.position.set(-15, 15, -15);
        scene.add(overhead2);
    },

    /**
     * Calculate required floor size based on all objects in the scene
     * @param {Array} works - Work stations
     * @param {Array} flowZones - Flow zones
     * @param {number} requestedSize - Requested floor size from config
     * @returns {number} - Required floor size
     */
    _calculateRequiredFloorSize: function(works, flowZones, requestedSize) {
        const minSize = 100;
        const margin = 20; // Extra margin around objects

        let maxX = 0, maxZ = 0, minX = 0, minZ = 0;
        let hasObjects = false;

        // Check works/stations positions
        if (works && works.length > 0) {
            works.forEach(work => {
                const x = work.posX || 0;
                const z = work.posZ || 0;
                maxX = Math.max(maxX, Math.abs(x));
                maxZ = Math.max(maxZ, Math.abs(z));
                minX = Math.min(minX, x);
                minZ = Math.min(minZ, z);
                hasObjects = true;
            });
        }

        // Check flow zones extents
        if (flowZones && flowZones.length > 0) {
            flowZones.forEach(zone => {
                const halfX = zone.sizeX / 2;
                const halfZ = zone.sizeZ / 2;
                maxX = Math.max(maxX, Math.abs(zone.centerX) + halfX);
                maxZ = Math.max(maxZ, Math.abs(zone.centerZ) + halfZ);
                minX = Math.min(minX, zone.centerX - halfX);
                minZ = Math.min(minZ, zone.centerZ - halfZ);
                hasObjects = true;
            });
        }

        if (!hasObjects) {
            return Math.max(requestedSize || minSize, minSize);
        }

        // Calculate required size (max extent in any direction * 2 + margin)
        const spanX = maxX - minX;
        const spanZ = maxZ - minZ;
        const maxSpan = Math.max(spanX, spanZ);
        const requiredSize = maxSpan + margin * 2;

        // Use the larger of calculated size, requested size, or minimum size
        const finalSize = Math.max(requiredSize, requestedSize || 0, minSize);

        console.log(`[Floor] Calculated required size: ${finalSize.toFixed(1)} (objects span: ${maxSpan.toFixed(1)}, requested: ${requestedSize || 0})`);

        return finalSize;
    },

    /**
     * Update floor and grid size dynamically
     * @param {THREE.Scene} scene - The scene
     * @param {number} newSize - New floor size
     */
    _updateFloorAndGrid: function(scene, newSize) {
        const ground = scene.getObjectByName('ground');
        const grid = scene.children.find(obj => obj.type === 'GridHelper');

        if (ground) {
            // Update ground plane
            ground.geometry.dispose();
            ground.geometry = new THREE.PlaneGeometry(newSize, newSize);
            console.log(`[Floor] Updated ground to ${newSize.toFixed(1)} × ${newSize.toFixed(1)}`);
        }

        if (grid) {
            // Remove old grid and create new one
            scene.remove(grid);
            grid.geometry.dispose();

            const gridDivisions = Math.max(Math.round(newSize / 2), 50);
            const newGrid = new THREE.GridHelper(newSize, gridDivisions, 0x6b7280, 0x52596380);
            newGrid.position.y = 0.01;
            scene.add(newGrid);
            console.log(`[Floor] Updated grid to ${newSize.toFixed(1)} with ${gridDivisions} divisions`);
        }

        // Update fog distance
        if (scene.fog) {
            scene.fog.near = newSize;
            scene.fog.far = newSize * 3;
        }
    },

    /**
     * Create floor and flow zones for device scene (without works)
     * @param {THREE.Scene} scene - Scene
     * @param {Array} flowZones - Flow zones
     * @param {number} floorSize - Floor size
     */
    _createDeviceSceneFloor: function(scene, flowZones, floorSize) {
        // Calculate floor size dynamically from FlowZones
        let minX = Infinity, maxX = -Infinity;
        let minZ = Infinity, maxZ = -Infinity;
        let floorCenterX = 0, floorCenterZ = 0;

        if (flowZones && flowZones.length > 0) {
            flowZones.forEach(zone => {
                const zoneMinX = zone.centerX - zone.sizeX / 2;
                const zoneMaxX = zone.centerX + zone.sizeX / 2;
                const zoneMinZ = zone.centerZ - zone.sizeZ / 2;
                const zoneMaxZ = zone.centerZ + zone.sizeZ / 2;

                minX = Math.min(minX, zoneMinX);
                maxX = Math.max(maxX, zoneMaxX);
                minZ = Math.min(minZ, zoneMinZ);
                maxZ = Math.max(maxZ, zoneMaxZ);
            });

            // Calculate floor dimensions with padding
            const padding = 40;
            const width = maxX - minX + padding * 2;
            const depth = maxZ - minZ + padding * 2;
            floorSize = Math.max(width, depth, 200);

            // Calculate floor center
            floorCenterX = (minX + maxX) / 2;
            floorCenterZ = (minZ + maxZ) / 2;

            console.log(`✓ Dynamic floor calculated from ${flowZones.length} zones: ${floorSize.toFixed(0)}x${floorSize.toFixed(0)}`);
            console.log(`  Zone bounds: X[${minX.toFixed(1)}, ${maxX.toFixed(1)}] Z[${minZ.toFixed(1)}, ${maxZ.toFixed(1)}]`);
        } else {
            floorSize = Math.max(floorSize || 200, 200);
        }

        const gridDivisions = Math.max(Math.round(floorSize / 5), 50);

        // Ground plane - centered on zones
        const groundGeo = new THREE.PlaneGeometry(floorSize, floorSize);
        const groundMat = new THREE.MeshStandardMaterial({
            color: 0x1e293b,  // Dark floor for device view
            roughness: 0.9,
            metalness: 0.1
        });
        const ground = new THREE.Mesh(groundGeo, groundMat);
        ground.rotation.x = -Math.PI / 2;
        ground.position.set(floorCenterX, -0.1, floorCenterZ);
        ground.receiveShadow = true;
        ground.name = 'ground';
        ground.userData.isFloor = true;
        scene.add(ground);

        // Grid - centered on zones
        const gridHelper = new THREE.GridHelper(floorSize, gridDivisions, 0x334155, 0x1e293b);
        gridHelper.position.set(floorCenterX, 0, floorCenterZ);
        scene.add(gridHelper);

        // Create Flow zones on the ground
        flowZones.forEach(zone => {
            const zoneGeo = new THREE.PlaneGeometry(zone.sizeX, zone.sizeZ);
            const zoneMat = new THREE.MeshStandardMaterial({
                color: zone.color,
                transparent: true,
                opacity: 0.15,
                roughness: 0.8,
                metalness: 0.2,
                side: THREE.DoubleSide
            });
            const zoneMesh = new THREE.Mesh(zoneGeo, zoneMat);
            zoneMesh.rotation.x = -Math.PI / 2;
            zoneMesh.position.set(zone.centerX, 0.02, zone.centerZ);
            zoneMesh.receiveShadow = true;
            zoneMesh.name = `flowZone_${zone.flowName}`;
            zoneMesh.userData = {
                isFlowZone: true,
                flowName: zone.flowName,
                color: zone.color
            };
            scene.add(zoneMesh);

            // Add border lines for flow zone
            const borderShape = new THREE.Shape();
            borderShape.moveTo(-zone.sizeX / 2, -zone.sizeZ / 2);
            borderShape.lineTo(zone.sizeX / 2, -zone.sizeZ / 2);
            borderShape.lineTo(zone.sizeX / 2, zone.sizeZ / 2);
            borderShape.lineTo(-zone.sizeX / 2, zone.sizeZ / 2);
            borderShape.lineTo(-zone.sizeX / 2, -zone.sizeZ / 2);

            const borderGeometry = new THREE.BufferGeometry().setFromPoints(borderShape.getPoints());
            const borderMaterial = new THREE.LineBasicMaterial({
                color: zone.color,
                transparent: true,
                opacity: 0.6,
                linewidth: 2
            });
            const border = new THREE.Line(borderGeometry, borderMaterial);
            border.rotation.x = -Math.PI / 2;
            border.position.set(zone.centerX, 0.03, zone.centerZ);
            border.name = `flowZone_${zone.flowName}_border`;
            scene.add(border);

            // Add Flow name text (elevated label at top-left corner)
            const label = this._createFlowZoneLabel(zone.flowName, zone.color);
            const labelX = zone.centerX - zone.sizeX / 2 + 2;
            const labelZ = zone.centerZ - zone.sizeZ / 2 - 1;  // Outside zone boundary
            label.position.set(labelX, 1.5, labelZ);
            label.name = `flowZone_${zone.flowName}_label`;
            scene.add(label);

            console.log(`Flow zone '${zone.flowName}' created at (${zone.centerX.toFixed(1)}, ${zone.centerZ.toFixed(1)}) size (${zone.sizeX.toFixed(1)}, ${zone.sizeZ.toFixed(1)})`);
        });

        console.log(`✓ Device scene floor created: ${floorSize}x${floorSize} with ${flowZones.length} flow zones`);
    },

    _createFactoryScene: function(scene, works, flowZones, floorSize) {
        // Use fixed floor size (don't auto-expand)
        floorSize = Math.max(floorSize || 200, 200);
        const gridDivisions = Math.max(Math.round(floorSize / 2), 50);

        // Ground plane - lighter factory floor color
        const groundGeo = new THREE.PlaneGeometry(floorSize, floorSize);
        const groundMat = new THREE.MeshStandardMaterial({
            color: 0x4a5563, // Lighter gray for factory floor
            roughness: 0.8,
            metalness: 0.2
        });
        const ground = new THREE.Mesh(groundGeo, groundMat);
        ground.rotation.x = -Math.PI / 2;
        ground.position.y = -0.02;
        ground.receiveShadow = true;
        ground.name = 'ground';
        ground.userData.isFloor = true; // Mark as floor to skip in click detection
        scene.add(ground);

        // Grid helper - more visible lines
        const grid = new THREE.GridHelper(floorSize, gridDivisions, 0x6b7280, 0x52596380);
        grid.position.y = 0.01;
        scene.add(grid);

        // Create Flow zones on the ground
        flowZones.forEach(zone => {
            const zoneGeo = new THREE.PlaneGeometry(zone.sizeX, zone.sizeZ);
            const zoneMat = new THREE.MeshStandardMaterial({
                color: zone.color,
                transparent: true,
                opacity: 0.15,
                roughness: 0.8,
                metalness: 0.2,
                side: THREE.DoubleSide
            });
            const zoneMesh = new THREE.Mesh(zoneGeo, zoneMat);
            zoneMesh.rotation.x = -Math.PI / 2;
            zoneMesh.position.set(zone.centerX, 0.02, zone.centerZ);
            zoneMesh.receiveShadow = true;
            zoneMesh.name = `flowZone_${zone.flowName}`;
            zoneMesh.userData = {
                isFlowZone: true,  // FIXED: Mark as Flow Zone instead of floor
                flowName: zone.flowName,
                color: zone.color
            };
            scene.add(zoneMesh);

            // Add border lines for flow zone
            const borderShape = new THREE.Shape();
            borderShape.moveTo(-zone.sizeX / 2, -zone.sizeZ / 2);
            borderShape.lineTo(zone.sizeX / 2, -zone.sizeZ / 2);
            borderShape.lineTo(zone.sizeX / 2, zone.sizeZ / 2);
            borderShape.lineTo(-zone.sizeX / 2, zone.sizeZ / 2);
            borderShape.lineTo(-zone.sizeX / 2, -zone.sizeZ / 2);

            const borderGeometry = new THREE.BufferGeometry().setFromPoints(borderShape.getPoints());
            const borderMaterial = new THREE.LineBasicMaterial({
                color: zone.color,
                transparent: true,
                opacity: 0.6,
                linewidth: 2
            });
            const border = new THREE.Line(borderGeometry, borderMaterial);
            border.rotation.x = -Math.PI / 2;
            border.position.set(zone.centerX, 0.03, zone.centerZ);
            border.name = `flowZone_${zone.flowName}_border`;  // FIXED: Added proper border naming
            scene.add(border);

            // Add Flow name text (elevated label at top-left corner)
            const label = this._createFlowZoneLabel(zone.flowName, zone.color);
            // Position at top-left corner of Flow Zone, elevated above devices
            const labelX = zone.centerX - zone.sizeX / 2 + 2;  // Slightly inset from left edge
            const labelZ = zone.centerZ - zone.sizeZ / 2 - 1;  // Outside zone boundary
            label.position.set(labelX, 1.5, labelZ);  // Elevated at y=1.5 to be visible above devices
            label.name = `flowZone_${zone.flowName}_label`;
            scene.add(label);

            console.log(`Flow zone '${zone.flowName}' created at (${zone.centerX.toFixed(1)}, ${zone.centerZ.toFixed(1)}) size (${zone.sizeX.toFixed(1)}, ${zone.sizeZ.toFixed(1)})`);
        });

        // Create station objects
        const stationMeshes = {};
        console.log(`Creating ${works.length} work stations...`);
        works.forEach((work, idx) => {
            const station = this._createStation(work, idx);
            scene.add(station.group);
            stationMeshes[work.id] = station.indicator;

            // Log station position
            console.log(`  Station[${idx}] '${work.name}' at (${station.group.position.x.toFixed(1)}, ${station.group.position.y.toFixed(1)}, ${station.group.position.z.toFixed(1)})`);
        });

        return stationMeshes;
    },

    _createConnections: function(scene, connections, stationMeshes, sceneData) {
        // Build work name to position mapping
        const workPositions = {};
        Object.keys(stationMeshes).forEach(workId => {
            const mesh = stationMeshes[workId];
            const group = mesh.parent;
            if (group) {
                workPositions[workId] = {
                    x: group.position.x,
                    y: group.position.y + 1.3, // Match indicator height
                    z: group.position.z
                };
            }
        });

        // Initialize work connection lines array if sceneData provided
        const workConnectionLines = [];

        connections.forEach(conn => {
            const fromPos = workPositions[conn.from];
            const toPos = workPositions[conn.to];

            if (!fromPos || !toPos) {
                console.warn(`Cannot create connection: ${conn.from} -> ${conn.to} (position not found)`);
                return;
            }

            // Create curved arrow using QuadraticBezierCurve3
            const start = new THREE.Vector3(fromPos.x, fromPos.y, fromPos.z);
            const end = new THREE.Vector3(toPos.x, toPos.y, toPos.z);

            // Calculate control point for curve (midpoint elevated)
            const distance = start.distanceTo(end);
            const elevation = Math.min(distance * 0.3, 5); // Natural curve elevation
            const midpoint = new THREE.Vector3(
                (fromPos.x + toPos.x) / 2,
                Math.max(fromPos.y, toPos.y) + elevation,
                (fromPos.z + toPos.z) / 2
            );

            const curve = new THREE.QuadraticBezierCurve3(start, midpoint, end);
            const points = curve.getPoints(50);
            const geometry = new THREE.BufferGeometry().setFromPoints(points);

            const material = new THREE.LineBasicMaterial({
                color: 0x06b6d4, // Cyan - more visible and modern
                linewidth: 2,
                transparent: true,
                opacity: 0.8
            });

            const line = new THREE.Line(geometry, material);
            line.userData.isWorkConnection = true;  // Mark as work connection
            scene.add(line);
            workConnectionLines.push(line);

            // Add arrow head at the end
            const arrowDir = new THREE.Vector3()
                .subVectors(end, points[points.length - 2])
                .normalize();
            const arrowHelper = new THREE.ArrowHelper(
                arrowDir,
                end,
                2, // Arrow length
                0x06b6d4, // Cyan - matching line color
                1.0, // Head length
                0.6  // Head width
            );
            arrowHelper.userData.isWorkConnection = true;  // Mark as work connection
            scene.add(arrowHelper);
            workConnectionLines.push(arrowHelper);

            console.log(`Connection created: ${conn.from} -> ${conn.to}`);
        });

        // Store work connection lines in sceneData if provided
        if (sceneData) {
            sceneData.workConnectionLines = workConnectionLines;
        }

        console.log(`Created ${connections.length} connections (${workConnectionLines.length} objects)`);
        return workConnectionLines;
    },

    _createStation: function(work, idx) {
        // Critical Fix 1.7: Validate input
        if (!work) {
            console.error('Cannot create station: work is null/undefined');
            return { group: new THREE.Group(), indicator: null };
        }

        const group = new THREE.Group();
        const xPos = work.posX !== undefined ? work.posX : (idx * 8 - 20);
        const yPos = work.posY !== undefined ? work.posY : 0;
        const zPos = work.posZ !== undefined ? work.posZ : 0;
        group.position.set(xPos, yPos, zPos);

        // Get work's device type (determines base station model)
        const workDeviceType = this._getWorkDeviceType(work);
        const stateColor = this.stateColors[work.state] || this.stateColors.R;

        console.log(`Work ${work.id} (${work.name}): deviceType="${workDeviceType}"`);

        // Realistic factory station dimensions
        const callCount = work.calls ? work.calls.length : 0;
        const baseWidth = Math.max(4, callCount * 0.7 + 2);
        const baseDepth = 3.5;

        // Metal materials
        const grayMetal = new THREE.MeshStandardMaterial({
            color: 0x606468,
            metalness: 0.8,
            roughness: 0.3
        });
        const darkMetal = new THREE.MeshStandardMaterial({
            color: 0x3a3d42,
            metalness: 0.7,
            roughness: 0.4
        });

        // Floor platform with supports
        const platformGeo = new THREE.BoxGeometry(baseWidth, 0.2, baseDepth);
        const platform = new THREE.Mesh(platformGeo, darkMetal);
        platform.position.y = 0.1;
        platform.castShadow = true;
        platform.receiveShadow = true;
        platform.name = `platform_${work.id}`;
        platform.userData.workId = work.id;  // Store workId for click detection
        platform.userData.workData = work;
        group.add(platform);

        // Corner support pillars
        const pillarGeo = new THREE.CylinderGeometry(0.15, 0.15, 2, 8);
        const pillarPositions = [
            [-baseWidth/2 + 0.5, 1, -baseDepth/2 + 0.5],
            [baseWidth/2 - 0.5, 1, -baseDepth/2 + 0.5],
            [-baseWidth/2 + 0.5, 1, baseDepth/2 - 0.5],
            [baseWidth/2 - 0.5, 1, baseDepth/2 - 0.5]
        ];
        pillarPositions.forEach((pos, pillarIdx) => {
            const pillar = new THREE.Mesh(pillarGeo, grayMetal);
            pillar.position.set(...pos);
            pillar.castShadow = true;
            pillar.name = `pillar_${work.id}_${pillarIdx}`;
            pillar.userData.workId = work.id;  // Store workId for click detection
            pillar.userData.workData = work;
            group.add(pillar);
        });

        // === CREATE MAIN DEVICE MODEL (based on work device type) ===
        const deviceSize = 0.8; // Larger size for work station model
        let mainDeviceModel;

        if (workDeviceType === 'robot') {
            mainDeviceModel = this._createRobotModel(deviceSize, stateColor);
            console.log(`  ✓ Created ROBOT work station for ${work.name}`);
        } else if (workDeviceType === 'small') {
            mainDeviceModel = this._createSmallDeviceModel(deviceSize, stateColor);
            console.log(`  ✓ Created SMALL DEVICE work station for ${work.name}`);
        } else {
            mainDeviceModel = this._createGeneralDeviceModel(deviceSize, stateColor);
            console.log(`  ✓ Created GENERAL work station for ${work.name}`);
        }

        // Position main device model at center of platform
        mainDeviceModel.position.set(0, 0.2, 0);
        mainDeviceModel.castShadow = true;

        // Store work data on main device
        mainDeviceModel.name = `device_${work.id}`;
        mainDeviceModel.userData.workId = work.id;
        mainDeviceModel.userData.workData = work;
        mainDeviceModel.userData.deviceType = workDeviceType;
        mainDeviceModel.userData.isRobot = (workDeviceType === 'robot');  // Enable animation for robot types
        mainDeviceModel.traverse(child => {
            child.userData.workId = work.id;
            child.userData.workData = work;
            child.userData.deviceType = workDeviceType;
        });

        group.add(mainDeviceModel);

        // Use main device model as indicator (for state updates)
        const indicator = mainDeviceModel;

        // Critical Fix 1.6: Progress bar (if total > 0 and valid data)
        if (work.total > 0 && work.elapsed >= 0) {
            const progressRatio = Math.min(1.0, Math.max(0, work.elapsed / work.total));
            const progressWidth = baseWidth * 0.85 * progressRatio;

            // Only show if meaningful progress
            if (progressWidth > 0.01) {
                const progressGeo = new THREE.BoxGeometry(progressWidth, 0.2, 0.3);
                const progressMat = new THREE.MeshStandardMaterial({
                    color: 0x10b981,
                    emissive: 0x10b981,
                    emissiveIntensity: 0.5
                });
                const progressBar = new THREE.Mesh(progressGeo, progressMat);
                progressBar.position.set(
                    -(baseWidth * 0.85) / 2 + progressWidth / 2,
                    0.5,
                    baseDepth * 0.45
                );
                progressBar.userData.workId = work.id;  // Store workId for click detection
                progressBar.userData.workData = work;
                group.add(progressBar);
            }
        }

        // Create call API indicators (small boxes on top of work station)
        if (work.calls && work.calls.length > 0) {
            const callSpacing = (baseWidth * 0.8) / Math.max(1, work.calls.length);
            const startX = -(baseWidth * 0.8) / 2 + callSpacing / 2;
            const callY = workDeviceType === 'robot' ? 3.5 : 2.5; // Higher for robot

            work.calls.forEach((call, callIdx) => {
                const callSize = 0.3; // Small box size
                const callStateColor = this.stateColors[call.state] || this.stateColors.R;

                // Simple box for API call
                const callBoxGeo = new THREE.BoxGeometry(callSize, callSize, callSize);
                const callBoxMat = new THREE.MeshStandardMaterial({
                    color: callStateColor.hex,
                    emissive: callStateColor.hex,
                    emissiveIntensity: 0.5,
                    metalness: 0.4,
                    roughness: 0.3
                });
                const callBox = new THREE.Mesh(callBoxGeo, callBoxMat);

                // Position above the work station
                callBox.position.set(
                    startX + callIdx * callSpacing,
                    callY,
                    baseDepth * 0.25
                );
                callBox.castShadow = true;

                // Store call data for click detection
                callBox.userData.callId = call.id;
                callBox.userData.callData = call;
                callBox.userData.workId = work.id;
                callBox.userData.isCallBox = true;

                // Add glow for errors
                if (call.hasError) {
                    const glowGeo = new THREE.SphereGeometry(callSize * 1.2, 16, 16);
                    const glowMat = new THREE.MeshBasicMaterial({
                        color: 0xff0000,
                        transparent: true,
                        opacity: 0.3
                    });
                    const glow = new THREE.Mesh(glowGeo, glowMat);
                    callBox.add(glow);
                }

                // Add condition indicators (small spheres above the box)
                let conditionOffset = 0;
                const indicatorHeight = callSize * 0.8;

                if (call.activeTrigger && call.activeTrigger !== '') {
                    const triggerGeo = new THREE.SphereGeometry(callSize * 0.2, 8, 8);
                    const triggerMat = new THREE.MeshBasicMaterial({ color: 0xfbbf24 });
                    const trigger = new THREE.Mesh(triggerGeo, triggerMat);
                    trigger.position.set(conditionOffset - callSize * 0.3, indicatorHeight, 0);
                    callBox.add(trigger);
                    conditionOffset += callSize * 0.25;
                }
                if (call.autoCondi && call.autoCondi !== '') {
                    const autoGeo = new THREE.SphereGeometry(callSize * 0.2, 8, 8);
                    const autoMat = new THREE.MeshBasicMaterial({ color: 0x3b82f6 });
                    const auto = new THREE.Mesh(autoGeo, autoMat);
                    auto.position.set(conditionOffset - callSize * 0.3, indicatorHeight, 0);
                    callBox.add(auto);
                    conditionOffset += callSize * 0.25;
                }
                if (call.commonCondi && call.commonCondi !== '') {
                    const commonGeo = new THREE.SphereGeometry(callSize * 0.2, 8, 8);
                    const commonMat = new THREE.MeshBasicMaterial({ color: 0x8b5cf6 });
                    const common = new THREE.Mesh(commonGeo, commonMat);
                    common.position.set(conditionOffset - callSize * 0.3, indicatorHeight, 0);
                    callBox.add(common);
                }

                group.add(callBox);

                // Call connections (thin lines between calls)
                if (call.hasNext && callIdx < work.calls.length - 1) {
                    const lineGeo = new THREE.BufferGeometry().setFromPoints([
                        new THREE.Vector3(
                            startX + callIdx * callSpacing + callSize / 2,
                            callY,
                            baseDepth * 0.25
                        ),
                        new THREE.Vector3(
                            startX + (callIdx + 1) * callSpacing - callSize / 2,
                            callY,
                            baseDepth * 0.25
                        )
                    ]);
                    const lineMat = new THREE.LineBasicMaterial({
                        color: 0x94a3b8,
                        linewidth: 1
                    });
                    const line = new THREE.Line(lineGeo, lineMat);
                    group.add(line);
                }

                console.log(`  Call[${callIdx}] ${call.id}: small box at y=${callY.toFixed(1)}`);
            });
        }

        // Device tags (small labels below work)
        if (work.devices && work.devices.length > 0) {
            const deviceText = work.devices.slice(0, 3).join(', ');
            if (deviceText) {
                const deviceLabel = this._createSimpleLabel(deviceText, 0x6b7280);
                deviceLabel.position.set(0, 0.4, -baseDepth * 0.6);
                deviceLabel.scale.set(1.5, 0.75, 1);
                deviceLabel.userData.workId = work.id;  // Store workId for click detection
                deviceLabel.userData.workData = work;
                group.add(deviceLabel);
            }
        }

        // Main label with detailed info
        const inCount = work.incomingCount || 0;
        const outCount = work.outgoingCount || 0;
        const detailInfo = `${work.name || 'Work ' + (idx + 1)}\n` +
                          `Calls: ${work.callCount || 0} | ` +
                          `${work.elapsed}/${work.total}s\n` +
                          `In: ${inCount} | Out: ${outCount}`;
        const label = this._createDetailedLabel(detailInfo, work.state);
        label.position.set(0, 3.5, 0);
        label.scale.set(3, 1.5, 1);
        // Store workId for click detection on label (name panel)
        label.userData.workId = work.id;
        label.userData.workData = work;
        label.userData.isLabel = true;
        group.add(label);

        // AAS icon floating above station
        const aasIcon = this._createAASIcon();
        aasIcon.position.set(0, 5.5, 0);  // Float above the station
        aasIcon.scale.set(1.5, 1.5, 1);

        // Store for animation and click detection
        aasIcon.userData.floatOffset = Math.random() * Math.PI * 2; // Random phase for each icon
        aasIcon.userData.isAASIcon = true;
        aasIcon.userData.baseY = 5.5; // Store base Y for animation
        aasIcon.userData.workId = work.id;  // Store workId for click detection
        aasIcon.userData.workData = work;
        group.add(aasIcon);

        return { group, indicator };
    },

    _createLabel: function(name, num) {
        const canvas = document.createElement('canvas');
        canvas.width = 256;
        canvas.height = 128;
        const ctx = canvas.getContext('2d');

        // Background
        ctx.fillStyle = '#1e293b';
        ctx.fillRect(0, 0, 256, 128);

        // Work name
        ctx.fillStyle = '#fff';
        ctx.font = 'bold 36px Arial';
        ctx.textAlign = 'center';
        ctx.fillText(name, 128, 50);

        // Work number
        ctx.font = '24px Arial';
        ctx.fillStyle = '#94a3b8';
        ctx.fillText(`Work ${num}`, 128, 90);

        const texture = new THREE.CanvasTexture(canvas);
        const spriteMat = new THREE.SpriteMaterial({ map: texture });
        const sprite = new THREE.Sprite(spriteMat);
        sprite.scale.set(4, 2, 1);
        return sprite;
    },

    /**
     * Create elevated Flow Zone label (billboard sprite)
     * @param {string} text - Flow name to display
     * @param {number} color - Text color (hex)
     * @returns {THREE.Sprite} - Label sprite
     */
    _createFlowZoneLabel: function(text, color = 0xffffff) {
        const canvas = document.createElement('canvas');
        canvas.width = 512;
        canvas.height = 128;
        const ctx = canvas.getContext('2d');

        // Draw dark background with border
        ctx.fillStyle = 'rgba(15, 23, 42, 0.9)';  // Dark semi-transparent background
        ctx.fillRect(0, 0, 512, 128);

        // Draw border
        ctx.strokeStyle = `#${color.toString(16).padStart(6, '0')}`;
        ctx.lineWidth = 4;
        ctx.strokeRect(4, 4, 504, 120);

        // Set font
        ctx.font = 'bold 64px Arial, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';

        // Draw text shadow
        ctx.fillStyle = 'rgba(0, 0, 0, 0.5)';
        ctx.fillText(text, 258, 66);

        // Draw main text
        ctx.fillStyle = `#${color.toString(16).padStart(6, '0')}`;
        ctx.fillText(text, 256, 64);

        const texture = new THREE.CanvasTexture(canvas);
        const spriteMat = new THREE.SpriteMaterial({
            map: texture,
            transparent: true,
            opacity: 1.0,
            depthWrite: false
        });
        const sprite = new THREE.Sprite(spriteMat);

        // Scale based on text length
        const baseScale = 6;
        const scaleX = Math.max(baseScale, text.length * 0.4);
        sprite.scale.set(scaleX, baseScale * 0.25, 1);

        return sprite;
    },

    /**
     * Create floor text for Flow Zone labels (text on the ground)
     * @param {string} text - Text to display
     * @param {number} color - Text color (hex)
     * @returns {THREE.Mesh} - Floor text mesh
     */
    _createFloorText: function(text, color = 0xffffff) {
        const canvas = document.createElement('canvas');
        canvas.width = 1024;  // Increased resolution for better clarity
        canvas.height = 256;
        const ctx = canvas.getContext('2d');

        // Draw semi-transparent background for better readability
        ctx.fillStyle = 'rgba(15, 23, 42, 0.85)';  // Dark background
        ctx.fillRect(0, 0, 1024, 256);

        // Draw colored border
        ctx.strokeStyle = `#${color.toString(16).padStart(6, '0')}`;
        ctx.lineWidth = 8;
        ctx.strokeRect(8, 8, 1008, 240);

        // Set font - larger and bolder
        ctx.font = 'bold 120px Arial, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';

        // Draw shadow first
        ctx.fillStyle = 'rgba(0, 0, 0, 0.8)';
        ctx.fillText(text, 516, 132);

        // Draw main text with color
        ctx.fillStyle = `#${color.toString(16).padStart(6, '0')}`;
        ctx.fillText(text, 512, 128);

        const texture = new THREE.CanvasTexture(canvas);
        texture.needsUpdate = true;

        // Larger size for better visibility
        const textLength = text.length;
        const width = Math.max(12, Math.min(25, textLength * 1.2));
        const height = 3;

        const geometry = new THREE.PlaneGeometry(width, height);
        const material = new THREE.MeshBasicMaterial({
            map: texture,
            transparent: true,
            opacity: 1.0,  // Full opacity
            side: THREE.DoubleSide,
            depthWrite: false
        });

        const mesh = new THREE.Mesh(geometry, material);
        mesh.rotation.x = -Math.PI / 2; // Lay flat on ground
        mesh.position.y = 0.08; // Slightly elevated to avoid z-fighting

        return mesh;
    },

    /**
     * Create device label with larger canvas for long names
     */
    _createDeviceLabel: function(name, apiDefCount, state) {
        const canvas = document.createElement('canvas');
        canvas.width = 512;  // Wider canvas for long device names
        canvas.height = 128;
        const ctx = canvas.getContext('2d');

        // Semi-transparent background
        ctx.fillStyle = 'rgba(30, 41, 59, 0.9)';
        ctx.roundRect(0, 0, 512, 128, 8);
        ctx.fill();

        // State color border
        const stateColor = this.stateColors[state] || this.stateColors.R;
        ctx.strokeStyle = `#${stateColor.hex.toString(16).padStart(6, '0')}`;
        ctx.lineWidth = 3;
        ctx.roundRect(0, 0, 512, 128, 8);
        ctx.stroke();

        // Device name (auto-size font based on name length)
        ctx.fillStyle = '#fff';
        let fontSize = 32;
        if (name.length > 20) fontSize = 24;
        if (name.length > 30) fontSize = 20;
        ctx.font = `bold ${fontSize}px Arial`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(name, 256, 50);

        // ApiDef count badge
        if (apiDefCount > 0) {
            ctx.font = '20px Arial';
            ctx.fillStyle = '#06b6d4';
            ctx.fillText(`${apiDefCount} ApiDefs`, 256, 95);
        }

        const texture = new THREE.CanvasTexture(canvas);
        const spriteMat = new THREE.SpriteMaterial({ map: texture, transparent: true });
        const sprite = new THREE.Sprite(spriteMat);
        sprite.scale.set(6, 1.5, 1);  // Wider scale
        sprite.userData.isLabel = true;
        return sprite;
    },

    _createDetailedLabel: function(text, state) {
        const canvas = document.createElement('canvas');
        canvas.width = 512;
        canvas.height = 256;
        const ctx = canvas.getContext('2d');

        // Background with state color
        const stateColor = this.stateColors[state] || this.stateColors.R;
        ctx.fillStyle = '#1e293b';
        ctx.fillRect(0, 0, 512, 256);

        // Border with state color
        ctx.strokeStyle = `#${stateColor.hex.toString(16).padStart(6, '0')}`;
        ctx.lineWidth = 4;
        ctx.strokeRect(0, 0, 512, 256);

        // Text lines
        const lines = text.split('\n');
        ctx.fillStyle = '#fff';
        ctx.textAlign = 'center';

        lines.forEach((line, idx) => {
            if (idx === 0) {
                ctx.font = 'bold 48px Arial';
                ctx.fillText(line, 256, 80);
            } else {
                ctx.font = '32px Arial';
                ctx.fillStyle = '#94a3b8';
                ctx.fillText(line, 256, 140 + idx * 40);
            }
        });

        const texture = new THREE.CanvasTexture(canvas);
        const spriteMat = new THREE.SpriteMaterial({ map: texture });
        const sprite = new THREE.Sprite(spriteMat);
        return sprite;
    },

    _createSimpleLabel: function(text, color) {
        const canvas = document.createElement('canvas');
        canvas.width = 256;
        canvas.height = 64;
        const ctx = canvas.getContext('2d');

        // Semi-transparent background
        ctx.fillStyle = 'rgba(30, 41, 59, 0.8)';
        ctx.fillRect(0, 0, 256, 64);

        // Text
        ctx.fillStyle = `#${color.toString(16).padStart(6, '0')}`;
        ctx.font = '20px Arial';
        ctx.textAlign = 'center';
        ctx.fillText(text, 128, 40);

        const texture = new THREE.CanvasTexture(canvas);
        const spriteMat = new THREE.SpriteMaterial({ map: texture });
        const sprite = new THREE.Sprite(spriteMat);
        return sprite;
    },

    _createAASIcon: function() {
        // Load AAS image texture
        const textureLoader = new THREE.TextureLoader();
        const texture = textureLoader.load('/images/aas.png');

        const spriteMat = new THREE.SpriteMaterial({
            map: texture,
            transparent: true,
            opacity: 0.95,
            depthTest: false // Always visible on top
        });
        const sprite = new THREE.Sprite(spriteMat);

        return sprite;
    },

    _fitCameraToScene: function(scene, camera, controls, works) {
        if (!works || works.length === 0) {
            console.log('[Camera] No works to fit camera to');
            return;
        }

        // Calculate bounding box of all work positions
        let minX = Infinity, maxX = -Infinity;
        let minZ = Infinity, maxZ = -Infinity;

        works.forEach(work => {
            const x = work.posX !== undefined ? work.posX : 0;
            const z = work.posZ !== undefined ? work.posZ : 0;
            minX = Math.min(minX, x);
            maxX = Math.max(maxX, x);
            minZ = Math.min(minZ, z);
            maxZ = Math.max(maxZ, z);
        });

        // Calculate center and size
        const centerX = (minX + maxX) / 2;
        const centerZ = (minZ + maxZ) / 2;
        const sizeX = maxX - minX;
        const sizeZ = maxZ - minZ;
        const maxSpan = Math.max(sizeX, sizeZ, 50);

        // Position camera at consistent angle (45 degrees from top-right)
        const cameraHeight = Math.max(maxSpan * 0.7, 40);
        const cameraDistance = Math.max(maxSpan * 1.2, 60);

        // Set camera to look at center from 45-degree angle
        camera.position.set(
            centerX + cameraDistance * 0.5,
            cameraHeight,
            centerZ + cameraDistance * 0.8
        );

        // Update controls target
        controls.target.set(centerX, 0, centerZ);
        controls.update();

        console.log(`[Camera] Fitted to scene: center=(${centerX.toFixed(1)}, ${centerZ.toFixed(1)}), span=${maxSpan.toFixed(1)}, ${works.length} objects`);
    },

    _handleResize: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const container = sceneData.container;
        const width = container.clientWidth;
        const height = container.clientHeight;

        sceneData.camera.aspect = width / height;
        sceneData.camera.updateProjectionMatrix();
        sceneData.renderer.setSize(width, height);
    },

    // ========== Interactive Features ==========

    _createFloorLabel: function(text, color) {
        const canvas = document.createElement('canvas');
        canvas.width = 512;
        canvas.height = 128;
        const ctx = canvas.getContext('2d');

        // Semi-transparent background
        ctx.fillStyle = `rgba(${(color >> 16) & 255}, ${(color >> 8) & 255}, ${color & 255}, 0.3)`;
        ctx.fillRect(0, 0, 512, 128);

        // Border
        ctx.strokeStyle = `rgb(${(color >> 16) & 255}, ${(color >> 8) & 255}, ${color & 255})`;
        ctx.lineWidth = 4;
        ctx.strokeRect(0, 0, 512, 128);

        // Text
        ctx.fillStyle = '#ffffff';
        ctx.font = 'bold 48px Arial';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(text, 256, 64);

        const texture = new THREE.CanvasTexture(canvas);
        const spriteMat = new THREE.SpriteMaterial({
            map: texture,
            transparent: true,
            opacity: 0.8
        });
        const sprite = new THREE.Sprite(spriteMat);
        sprite.scale.set(10, 2.5, 1);
        return sprite;
    },

    _setupInteractionHandlers: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const container = sceneData.container;
        const canvas = sceneData.renderer.domElement;

        // Initialize drag state
        sceneData.dragState = {
            isDragging: false,
            dragObject: null,
            dragPlane: new THREE.Plane(),
            dragOffset: new THREE.Vector3(),
            mouseDownPos: new THREE.Vector2(),
            mouseMoveThreshold: 5 // pixels to distinguish click from drag
        };

        // Click handler for normal clicks (Call cubes, Work stations)
        const clickHandler = (event) => {
            // Ignore if dragging just finished
            if (sceneData.dragState.isDragging) {
                return;
            }
            this._handleClick(elementId, event);
        };
        canvas.addEventListener('click', clickHandler);
        sceneData.clickHandler = clickHandler;

        // Mouse down/move/up for Alt + drag only
        const mouseDownHandler = (event) => this._handleMouseDown(elementId, event);
        canvas.addEventListener('mousedown', mouseDownHandler);
        sceneData.mouseDownHandler = mouseDownHandler;

        const mouseMoveHandler = (event) => this._handleMouseMove(elementId, event);
        canvas.addEventListener('mousemove', mouseMoveHandler);
        sceneData.mouseMoveHandler = mouseMoveHandler;

        const mouseUpHandler = (event) => this._handleMouseUp(elementId, event);
        canvas.addEventListener('mouseup', mouseUpHandler);
        sceneData.mouseUpHandler = mouseUpHandler;

        // Set cursor style
        canvas.style.cursor = 'pointer';

        console.log(`Interaction handlers setup for ${elementId}`);
    },

    _handleClick: function(elementId, event) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Ignore clicks in Edit Mode or if Alt key is pressed (dragging is happening)
        const isDragMode = sceneData.editMode || (event.altKey || sceneData.altPressed);
        if (isDragMode) {
            console.log('Click ignored - Drag Mode is active (Edit Mode or Alt key)');
            return;
        }

        const intersects = this._getRaycasterIntersects(elementId, event);

        // Debug: Log all intersects with details
        console.log(`=== Click Debug ===`);
        console.log(`Total intersects: ${intersects.length}`);
        if (intersects.length > 0) {
            intersects.slice(0, 5).forEach((hit, i) => {
                const obj = hit.object;
                console.log(`  [${i}] type=${obj.type}, name="${obj.name || '(no name)'}", isFloor=${obj.userData?.isFloor || false}, workId=${obj.userData?.workId || 'NONE'}, callId=${obj.userData?.callId || 'NONE'}`);
            });
        } else {
            console.log('  No objects hit by raycaster - clicking on empty space');
        }

        // Find the first non-floor object in the intersects
        const nonFloorIntersects = intersects.filter(hit => !hit.object.userData?.isFloor);
        console.log(`  Non-floor intersects: ${nonFloorIntersects.length}`);

        if (nonFloorIntersects.length > 0) {
            const clickedObject = nonFloorIntersects[0].object;

            // Check if this is a Call cube (Work-based)
            if (clickedObject.userData && clickedObject.userData.callId) {
                const callId = clickedObject.userData.callId;
                const callData = clickedObject.userData.callData;
                // Use workId directly from userData (already set when callBox is created)
                const callWorkId = clickedObject.userData.workId;

                console.log('=== Call cube clicked ===');
                console.log('Call ID:', callId);
                console.log('Work ID:', callWorkId);
                console.log('Call Data:', JSON.stringify(callData, null, 2));
                console.log('Call Data keys:', Object.keys(callData || {}));

                // Toggle call chain visualization
                if (sceneData.selectedCall === callId) {
                    // Deselect call
                    console.log('Deselecting call');
                    this._clearCallChainVisualization(elementId);
                    sceneData.selectedCall = null;
                } else {
                    // Select and visualize call chain
                    console.log('Selecting call and visualizing chain');
                    sceneData.selectedCall = callId;
                    this._visualizeCallChain(elementId, callId, callData, clickedObject);

                    // Call Blazor callback to show equipment tooltip (arrows already visualized in 3D)
                    if (sceneData.callSelectionCallback) {
                        const screenPos = this._getScreenPosition(elementId, clickedObject);
                        console.log(`✓ Calling Blazor callback for call selection at (${screenPos.x.toFixed(0)}, ${screenPos.y.toFixed(0)})`);
                        sceneData.callSelectionCallback.invokeMethodAsync('OnCallSelected', callId, callData, screenPos.x, screenPos.y)
                            .then(() => console.log('✓ Blazor callback completed'))
                            .catch(err => console.error('✗ Blazor callback error:', err));
                    } else {
                        console.warn('⚠️ No call selection callback registered');
                    }
                }
                return;
            }

            // Check if this is an ApiDef cube (Device-based)
            if (clickedObject.userData && clickedObject.userData.isApiDefCube) {
                const apiDefId = clickedObject.userData.apiDefId;
                const apiDefData = clickedObject.userData.apiDefData;
                const deviceId = clickedObject.userData.deviceId;

                console.log('=== ApiDef cube clicked ===');
                console.log('ApiDef ID:', apiDefId);
                console.log('Device ID:', deviceId);
                console.log('ApiDef Data:', apiDefData);

                // Toggle ApiDef selection
                if (sceneData.selectedApiDef === apiDefId) {
                    // Deselect
                    console.log('Deselecting ApiDef');
                    this._clearApiDefVisualization(elementId);
                    sceneData.selectedApiDef = null;
                } else {
                    // Select and visualize
                    console.log('Selecting ApiDef and visualizing');
                    sceneData.selectedApiDef = apiDefId;
                    this._visualizeApiDefConnection(elementId, apiDefId, deviceId, clickedObject);

                    // Call Blazor callback with correct argument order: deviceId, apiDefName
                    if (sceneData.callSelectionCallback) {
                        const screenPos = this._getScreenPosition(elementId, clickedObject);
                        const apiDefName = apiDefData?.name || 'Unknown';
                        console.log(`✓ Calling Blazor callback for ApiDef selection: device=${deviceId}, apiDef=${apiDefName}`);
                        sceneData.callSelectionCallback.invokeMethodAsync('OnApiDefSelected', deviceId, apiDefName)
                            .then(() => console.log('✓ ApiDef callback completed'))
                            .catch(err => console.error('✗ ApiDef callback error:', err));
                    }
                }
                return;
            }

            // High Priority Fix 2.1: Find the work this object belongs to
            // First, check userData.workId directly on clicked object and parents
            let workId = null;
            let deviceId = null;
            let searchObj = clickedObject;
            let maxIterations = 20; // Safety limit
            let iterations = 0;

            // Method 1: Check userData.workId or userData.deviceId on clicked object and its parents
            while (searchObj && !workId && !deviceId && iterations < maxIterations) {
                iterations++;
                if (searchObj.userData) {
                    if (searchObj.userData.workId) {
                        workId = searchObj.userData.workId;
                        console.log(`Found workId via userData: ${workId}`);
                        break;
                    }
                    if (searchObj.userData.deviceId) {
                        deviceId = searchObj.userData.deviceId;
                        console.log(`Found deviceId via userData: ${deviceId}`);
                        break;
                    }
                }
                searchObj = searchObj.parent;
            }

            // Method 2: Fallback - check stationMeshes mapping (for works)
            if (!workId && !deviceId && sceneData.stationMeshes) {
                iterations = 0;
                searchObj = clickedObject;
                while (searchObj && !workId && iterations < maxIterations) {
                    iterations++;
                    for (const [id, mesh] of Object.entries(sceneData.stationMeshes)) {
                        if (mesh === searchObj || mesh.parent === searchObj) {
                            workId = id;
                            console.log(`Found workId via stationMeshes: ${workId}`);
                            break;
                        }
                    }
                    searchObj = searchObj.parent;
                }
            }

            // Method 3: Fallback - check deviceMeshes mapping (for devices)
            if (!workId && !deviceId && sceneData.deviceMeshes) {
                iterations = 0;
                searchObj = clickedObject;
                while (searchObj && !deviceId && iterations < maxIterations) {
                    iterations++;
                    for (const [id, mesh] of Object.entries(sceneData.deviceMeshes)) {
                        if (mesh === searchObj || mesh.parent === searchObj) {
                            deviceId = id;
                            console.log(`Found deviceId via deviceMeshes: ${deviceId}`);
                            break;
                        }
                    }
                    searchObj = searchObj.parent;
                }
            }

            if (iterations >= maxIterations) {
                console.warn('Max parent traversal iterations reached - possible circular reference');
            }

            if (workId) {
                console.log(`=== WorkStation clicked: ${workId} ===`);
                console.log(`  Current selected: ${sceneData.selectedObject}`);
                console.log(`  Clicked object type: ${clickedObject.type}, isLabel: ${clickedObject.userData?.isLabel}`);

                // Clear ApiDef connections when clicking on Work/Device
                this._clearApiDefConnections(elementId);
                sceneData.selectedApiDef = null;

                // Always show Equipment tooltip when clicking on WorkStation (label/platform/device)
                // Calculate screen position for tooltip
                const screenPos = this._getScreenPosition(elementId, clickedObject);
                if (sceneData.callSelectionCallback) {
                    console.log(`✓ Calling Blazor callback for WorkStation selection: ${workId} at (${screenPos.x.toFixed(0)}, ${screenPos.y.toFixed(0)})`);
                    sceneData.callSelectionCallback.invokeMethodAsync('OnWorkStationSelected', workId, screenPos.x, screenPos.y)
                        .then(() => console.log('✓ WorkStation selection callback completed'))
                        .catch(err => console.error('✗ WorkStation selection callback error:', err));
                } else {
                    console.warn('⚠️ No callSelectionCallback registered');
                }

                // Toggle highlight selection
                if (sceneData.selectedObject === workId) {
                    // Deselect - clear highlights but tooltip was already shown above
                    this._clearHighlights(elementId);
                    sceneData.selectedObject = null;
                } else {
                    // Select and highlight connections
                    sceneData.selectedObject = workId;
                    this._highlightConnections(elementId, workId);
                }
            } else if (deviceId) {
                console.log(`=== Device clicked: ${deviceId} ===`);
                console.log(`  Current selected: ${sceneData.selectedObject}`);

                // Clear ApiDef connections when clicking on Work/Device
                this._clearApiDefConnections(elementId);
                sceneData.selectedApiDef = null;

                // Find device data from devices array
                const deviceData = sceneData.devices?.find(d => d.id === deviceId) || {};

                // Calculate screen position for tooltip
                const screenPos = this._getScreenPosition(elementId, clickedObject);
                if (sceneData.callSelectionCallback) {
                    console.log(`✓ Calling Blazor callback for Device selection: ${deviceId} at (${screenPos.x.toFixed(0)}, ${screenPos.y.toFixed(0)})`);
                    // Use OnDeviceSelected callback with device data
                    sceneData.callSelectionCallback.invokeMethodAsync('OnDeviceSelected', deviceId, deviceData, screenPos.x, screenPos.y)
                        .then(() => console.log('✓ Device selection callback completed'))
                        .catch(err => console.error('✗ Device selection callback error:', err));
                }

                // Toggle highlight selection
                if (sceneData.selectedObject === deviceId) {
                    this._clearHighlights(elementId);
                    sceneData.selectedObject = null;
                } else {
                    sceneData.selectedObject = deviceId;
                    var deviceMesh = sceneData.deviceMeshes[deviceId];
                    if (deviceMesh) {
                        this._highlightDevice(elementId, deviceMesh);
                    }
                }
            } else {
                console.log('No workId or deviceId found for clicked object');
            }
        } else {
            // Clicked on empty space - clear selection
            this._clearHighlights(elementId);
            this._clearCallChainVisualization(elementId);
            this._clearApiDefConnections(elementId);
            sceneData.selectedObject = null;
            sceneData.selectedCall = null;
            sceneData.selectedApiDef = null;

            // Close equipment tooltip when clicking empty space
            if (sceneData.callSelectionCallback) {
                sceneData.callSelectionCallback.invokeMethodAsync('OnEmptySpaceClicked')
                    .catch(err => console.debug('OnEmptySpaceClicked callback not available:', err.message));
            }
        }
    },

    _handleMouseMove: function(elementId, event) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const dragState = sceneData.dragState;
        const canvas = sceneData.renderer.domElement;
        const rect = canvas.getBoundingClientRect();

        // Check if we should start dragging
        if (dragState.dragObject && !dragState.isDragging && !dragState.flowZoneOperation) {
            const currentX = event.clientX - rect.left;
            const currentY = event.clientY - rect.top;
            const dx = currentX - dragState.mouseDownPos.x;
            const dy = currentY - dragState.mouseDownPos.y;
            const distance = Math.sqrt(dx * dx + dy * dy);

            // Start dragging if moved beyond threshold
            if (distance > dragState.mouseMoveThreshold) {
                dragState.isDragging = true;
                console.log(`Started dragging work: ${dragState.workId}`);

                // Change cursor
                canvas.style.cursor = 'move';
            }
        }

        // Medium Priority Fix 3.2: Handle dragging with reusable objects
        if (dragState.isDragging && dragState.dragObject) {
            sceneData.reusableVector2.set(
                ((event.clientX - rect.left) / rect.width) * 2 - 1,
                -((event.clientY - rect.top) / rect.height) * 2 + 1
            );

            sceneData.reusableRaycaster.setFromCamera(sceneData.reusableVector2, sceneData.camera);

            if (sceneData.reusableRaycaster.ray.intersectPlane(
                dragState.dragPlane,
                sceneData.reusableVector3
            )) {
                // Update object position with offset
                dragState.dragObject.position.copy(sceneData.reusableVector3).add(dragState.dragOffset);

                // Snap to grid if enabled (grid cell size = 5)
                if (sceneData.gridSnap) {
                    const gs = 5;
                    dragState.dragObject.position.x = Math.round(dragState.dragObject.position.x / gs) * gs;
                    dragState.dragObject.position.z = Math.round(dragState.dragObject.position.z / gs) * gs;
                }

                // Keep Y position fixed
                dragState.dragObject.position.y = 0;

                // Real-time Flow Zone preview during drag (debounced for performance)
                this._recalculateFlowZonesDebounced(elementId, 200);
            }

            return; // Skip hover effects while dragging
        }

        // Restore cursor if not dragging
        if (!dragState.isDragging) {
            canvas.style.cursor = 'pointer';
        }

        // Hover effects (only when not dragging)
        if (!dragState.isDragging) {
            const intersects = this._getRaycasterIntersects(elementId, event);

            if (intersects.length > 0) {
                const hoveredObject = intersects[0].object;

                // Apply hover effect
                if (hoveredObject !== sceneData.hoveredObject) {
                    // Remove previous hover effect
                    if (sceneData.hoveredObject && sceneData.hoveredObject.material) {
                        sceneData.hoveredObject.material.emissiveIntensity =
                            sceneData.hoveredObject.userData.originalEmissive || 0.3;
                    }

                    // Apply new hover effect
                    if (hoveredObject.material && hoveredObject.material.emissive) {
                        hoveredObject.userData.originalEmissive = hoveredObject.material.emissiveIntensity;
                        hoveredObject.material.emissiveIntensity = 0.8;
                    }

                    sceneData.hoveredObject = hoveredObject;
                }
            } else {
                // Remove hover effect
                if (sceneData.hoveredObject && sceneData.hoveredObject.material) {
                    sceneData.hoveredObject.material.emissiveIntensity =
                        sceneData.hoveredObject.userData.originalEmissive || 0.3;
                    sceneData.hoveredObject = null;
                }
            }
        }
    },

    _getRaycasterIntersects: function(elementId, event) {
        // High Priority Fix 2.4: Enhanced input validation
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.scene || !sceneData.camera) {
            return [];
        }

        const canvas = sceneData.renderer?.domElement;
        if (!canvas || !canvas.isConnected) {
            return []; // Canvas not in DOM
        }

        // Validate event has required properties
        if (!event || (event.clientX === undefined && !event.touches)) {
            console.warn('Invalid event object for raycasting');
            return [];
        }

        const rect = canvas.getBoundingClientRect();

        // Handle both mouse and touch events
        const clientX = event.clientX !== undefined ? event.clientX :
                        (event.touches && event.touches[0] ? event.touches[0].clientX : 0);
        const clientY = event.clientY !== undefined ? event.clientY :
                        (event.touches && event.touches[0] ? event.touches[0].clientY : 0);

        // Calculate mouse position in normalized device coordinates (-1 to +1)
        const mouse = new THREE.Vector2(
            ((clientX - rect.left) / rect.width) * 2 - 1,
            -((clientY - rect.top) / rect.height) * 2 + 1
        );

        const raycaster = new THREE.Raycaster();
        raycaster.setFromCamera(mouse, sceneData.camera);

        // Get all meshes AND sprites from the scene (sprites for labels/name panels)
        const clickableObjects = [];
        sceneData.scene.traverse((obj) => {
            if (obj.isMesh || obj.isSprite) {
                clickableObjects.push(obj);
            }
        });

        return raycaster.intersectObjects(clickableObjects);
    },

    _handleMouseDown: function(elementId, event) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Ignore right-click
        if (event.button !== 0) return;

        const canvas = sceneData.renderer.domElement;
        const rect = canvas.getBoundingClientRect();

        // Store mouse down position
        sceneData.dragState.mouseDownPos.set(
            event.clientX - rect.left,
            event.clientY - rect.top
        );

        // Check if Edit Mode (Move Mode) is enabled
        if (!sceneData.editMode) {
            return; // Let click event handle normal clicks
        }

        console.log('✓ Move mode active, preparing for drag...');

        // Prevent event from reaching OrbitControls
        event.preventDefault();
        event.stopPropagation();

        const intersects = this._getRaycasterIntersects(elementId, event);
        if (intersects.length === 0) {
            return; // Nothing to drag
        }

        const clickedObject = intersects[0].object;

        // Skip Call cubes for drag
        if (clickedObject.userData && clickedObject.userData.callId) {
            return;
        }

        // Find work station or device group (with infinite loop prevention)
        let workId = null;
        let deviceId = null;
        let stationGroup = null;
        let parent = clickedObject;
        let maxIterations = 20; // Safety limit
        let iterations = 0;

        // First check for Work in stationMeshes
        while (parent && !workId && !deviceId && iterations < maxIterations) {
            iterations++;
            // Check if this object is in stationMeshes (Works)
            // stationMeshes[id] stores the indicator mesh, so check mesh.parent for the group
            if (sceneData.stationMeshes) {
                for (const [id, mesh] of Object.entries(sceneData.stationMeshes)) {
                    if (mesh === parent || mesh.parent === parent) {
                        workId = id;
                        stationGroup = mesh.parent;  // indicator.parent = group
                        break;
                    }
                }
            }
            // Check if this object is in deviceMeshes (Devices)
            // deviceMeshes[id] stores the group directly, not a child mesh
            if (!workId && sceneData.deviceMeshes) {
                for (const [id, group] of Object.entries(sceneData.deviceMeshes)) {
                    if (group === parent) {
                        deviceId = id;
                        stationGroup = group;  // group is already the station group
                        break;
                    }
                }
            }
            parent = parent.parent;
        }

        if (iterations >= maxIterations) {
            console.warn('Max parent traversal iterations reached in mousedown - possible circular reference');
        }

        if ((workId || deviceId) && stationGroup) {
            // Prepare for drag in Edit Mode
            sceneData.dragState.dragObject = stationGroup;
            sceneData.dragState.workId = workId;
            sceneData.dragState.deviceId = deviceId;

            // Calculate intersection point on XZ plane (ground level)
            const planeNormal = new THREE.Vector3(0, 1, 0);
            const planePoint = stationGroup.position.clone();
            sceneData.dragState.dragPlane.setFromNormalAndCoplanarPoint(planeNormal, planePoint);

            const raycaster = new THREE.Raycaster();
            const mouse = new THREE.Vector2(
                ((event.clientX - rect.left) / rect.width) * 2 - 1,
                -((event.clientY - rect.top) / rect.height) * 2 + 1
            );
            raycaster.setFromCamera(mouse, sceneData.camera);

            const intersectionPoint = new THREE.Vector3();
            raycaster.ray.intersectPlane(sceneData.dragState.dragPlane, intersectionPoint);

            // Store offset from intersection point to object position
            sceneData.dragState.dragOffset.copy(stationGroup.position).sub(intersectionPoint);

            const targetType = workId ? 'work' : 'device';
            const targetId = workId || deviceId;
            console.log(`✓ Edit Mode drag prepared for ${targetType}: ${targetId}`);
        }
    },

    _handleMouseUp: function(elementId, event) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const dragState = sceneData.dragState;
        const canvas = sceneData.renderer.domElement;

        if (dragState.isDragging) {
            // End drag
            dragState.isDragging = false;
            const targetType = dragState.workId ? 'work' : 'device';
            const targetId = dragState.workId || dragState.deviceId;
            console.log(`Drag ended for ${targetType}: ${targetId}`);

            // Save positions to localStorage (both works and devices)
            this._saveWorkPositions(elementId);
            if (dragState.deviceId) {
                this._saveDevicePositions(elementId, this._collectDevicePositions(sceneData));
            }

            // Auto-recalculate Flow Zones based on device/work positions (immediate)
            this._recalculateFlowZones(elementId);

            // Restore cursor (keep 'grab' if still in edit mode)
            canvas.style.cursor = sceneData.editMode ? 'grab' : 'pointer';

            dragState.dragObject = null;
            dragState.workId = null;
            dragState.deviceId = null;
        } else if (dragState.dragObject) {
            // Drag was prepared but no drag occurred - just clear state
            dragState.dragObject = null;
            dragState.workId = null;
            dragState.deviceId = null;
        }
        // Note: click event will handle normal clicks
    },

    /**
     * Save all work positions to localStorage
     * @param {string} elementId - Scene identifier
     */
    _saveWorkPositions: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const positions = {};
        Object.keys(sceneData.stationMeshes).forEach(workId => {
            const mesh = sceneData.stationMeshes[workId];
            const group = mesh.parent;
            if (group) {
                positions[workId] = {
                    x: group.position.x,
                    y: group.position.y,
                    z: group.position.z
                };
            }
        });

        const storageKey = `ev2-3d-layout-${elementId}`;
        // High Priority Fix 2.2: Handle localStorage quota exceeded errors
        try {
            const data = JSON.stringify(positions);
            localStorage.setItem(storageKey, data);
            console.log(`✓ Saved ${Object.keys(positions).length} work positions (${data.length} bytes)`);
        } catch (error) {
            if (error.name === 'QuotaExceededError') {
                console.error('❌ localStorage quota exceeded - clearing old data');
                // Try to clear and retry
                try {
                    localStorage.removeItem(storageKey);
                    localStorage.setItem(storageKey, JSON.stringify(positions));
                    console.log('✓ Saved after clearing old data');
                } catch (retryError) {
                    console.error('❌ Still failed after clearing:', retryError);
                    alert('Unable to save layout: Storage quota exceeded');
                }
            } else {
                console.error('Failed to save work positions:', error);
            }
        }
    },

    /**
     * Load work positions from localStorage
     * @param {string} elementId - Scene identifier
     * @returns {object|null} Positions object or null if not found
     */
    _loadWorkPositions: function(elementId) {
        const storageKey = `ev2-3d-layout-${elementId}`;
        try {
            const data = localStorage.getItem(storageKey);
            if (data) {
                return JSON.parse(data);
            }
        } catch (error) {
            console.error('Failed to load work positions:', error);
        }
        return null;
    },

    /**
     * Clear saved work positions from localStorage
     * @param {string} elementId - Scene identifier
     */
    _clearSavedPositions: function(elementId) {
        const storageKey = `ev2-3d-layout-${elementId}`;
        try {
            localStorage.removeItem(storageKey);
            console.log(`✓ Cleared saved work positions for ${elementId}`);
        } catch (error) {
            console.error('Failed to clear saved positions:', error);
        }
    },

    /**
     * Get screen position of a 3D object for tooltip positioning
     * @param {string} elementId - Scene identifier
     * @param {Object3D} object3D - Three.js object
     * @returns {{x: number, y: number}} Screen coordinates
     */
    _getScreenPosition: function(elementId, object3D) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !object3D) {
            return { x: 100, y: 100 }; // Default fallback
        }

        // Get world position of the object
        const vector = new THREE.Vector3();
        object3D.getWorldPosition(vector);

        // Project to screen coordinates
        vector.project(sceneData.camera);

        // Convert to screen coordinates
        const canvas = sceneData.renderer.domElement;
        const x = (vector.x * 0.5 + 0.5) * canvas.clientWidth;
        const y = (-vector.y * 0.5 + 0.5) * canvas.clientHeight;

        // Offset tooltip to the right and up from the clicked position
        return {
            x: Math.min(x + 20, canvas.clientWidth - 300),  // Keep tooltip on screen
            y: Math.max(y - 50, 10)
        };
    },

    _highlightConnections: function(elementId, workId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Clear previous highlights
        this._clearHighlights(elementId);

        // Find the work data
        const workData = sceneData.config.works.find(w => w.id === workId);
        if (!workData) return;

        console.log(`Highlighting connections for work: ${workId}`);
        console.log(`Incoming: ${workData.incoming ? workData.incoming.length : 0}, Outgoing: ${workData.outgoing ? workData.outgoing.length : 0}`);

        // Highlight the selected work with a strong glow
        const selectedMesh = sceneData.stationMeshes[workId];
        if (selectedMesh && selectedMesh.material) {
            selectedMesh.material.emissiveIntensity = 1.0;
            sceneData.highlightedObjects.push(selectedMesh);
        }

        // Draw connection lines to incoming works (cyan)
        if (workData.incoming && workData.incoming.length > 0) {
            workData.incoming.forEach(incomingId => {
                const line = this._createConnectionLine(
                    elementId, incomingId, workId, 0x06b6d4, true
                );
                if (line) {
                    sceneData.scene.add(line);
                    sceneData.connectionLines.push(line);
                }

                // Highlight incoming work
                const incomingMesh = sceneData.stationMeshes[incomingId];
                if (incomingMesh && incomingMesh.material) {
                    incomingMesh.material.emissiveIntensity = 0.7;
                    sceneData.highlightedObjects.push(incomingMesh);
                }
            });
        }

        // Draw connection lines to outgoing works (amber)
        if (workData.outgoing && workData.outgoing.length > 0) {
            workData.outgoing.forEach(outgoingId => {
                const line = this._createConnectionLine(
                    elementId, workId, outgoingId, 0xfbbf24, false
                );
                if (line) {
                    sceneData.scene.add(line);
                    sceneData.connectionLines.push(line);
                }

                // Highlight outgoing work
                const outgoingMesh = sceneData.stationMeshes[outgoingId];
                if (outgoingMesh && outgoingMesh.material) {
                    outgoingMesh.material.emissiveIntensity = 0.7;
                    sceneData.highlightedObjects.push(outgoingMesh);
                }
            });
        }
    },

    _createConnectionLine: function(elementId, fromWorkId, toWorkId, color, isIncoming) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return null;

        const fromMesh = sceneData.stationMeshes[fromWorkId];
        const toMesh = sceneData.stationMeshes[toWorkId];

        if (!fromMesh || !toMesh) return null;

        const fromGroup = fromMesh.parent;
        const toGroup = toMesh.parent;

        if (!fromGroup || !toGroup) return null;

        const fromPos = new THREE.Vector3(
            fromGroup.position.x,
            fromGroup.position.y + 2,
            fromGroup.position.z
        );
        const toPos = new THREE.Vector3(
            toGroup.position.x,
            toGroup.position.y + 2,
            toGroup.position.z
        );

        // Create curved line with bezier
        const midpoint = new THREE.Vector3(
            (fromPos.x + toPos.x) / 2,
            Math.max(fromPos.y, toPos.y) + 4,
            (fromPos.z + toPos.z) / 2
        );

        const curve = new THREE.QuadraticBezierCurve3(fromPos, midpoint, toPos);
        const points = curve.getPoints(50);
        const geometry = new THREE.BufferGeometry().setFromPoints(points);

        const material = new THREE.LineBasicMaterial({
            color: color,
            linewidth: 3,
            transparent: true,
            opacity: 0.9
        });

        const line = new THREE.Line(geometry, material);

        // Add arrow at the end
        const arrowDir = new THREE.Vector3()
            .subVectors(toPos, points[points.length - 2])
            .normalize();
        const arrowHelper = new THREE.ArrowHelper(
            arrowDir, toPos, 2.5, color, 1.5, 1.0
        );
        line.add(arrowHelper);

        return line;
    },

    /**
     * Highlight a device group (emissive glow on all meshes in the group)
     */
    _highlightDevice: function(elementId, deviceGroup) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !deviceGroup) return;

        let highlightCount = 0;
        deviceGroup.traverse(function(child) {
            if (child.isMesh && child.material) {
                if (child.material.emissive) {
                    child.userData._origEmissiveHex = child.material.emissive.getHex();
                    child.userData._origEmissiveIntensity = child.material.emissiveIntensity;
                    child.material.emissive.setHex(0x3b82f6);
                    child.material.emissiveIntensity = 0.8;
                    sceneData.highlightedObjects.push(child);
                    highlightCount++;
                }
            }
        });
        console.log(`✓ _highlightDevice: ${highlightCount} meshes highlighted`);
    },

    _clearHighlights: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Remove connection lines
        if (sceneData.connectionLines) {
            sceneData.connectionLines.forEach(line => {
                sceneData.scene.remove(line);
                if (line.geometry) line.geometry.dispose();
                if (line.material) line.material.dispose();
            });
            sceneData.connectionLines = [];
        }

        // Remove work connection lines (Work 간 화살표)
        if (sceneData.workConnectionLines) {
            sceneData.workConnectionLines.forEach(obj => {
                sceneData.scene.remove(obj);
                if (obj.geometry) obj.geometry.dispose();
                if (obj.material) {
                    if (Array.isArray(obj.material)) {
                        obj.material.forEach(m => m.dispose());
                    } else {
                        obj.material.dispose();
                    }
                }
                // ArrowHelper has children (line and cone)
                if (obj.children) {
                    obj.children.forEach(child => {
                        if (child.geometry) child.geometry.dispose();
                        if (child.material) child.material.dispose();
                    });
                }
            });
            sceneData.workConnectionLines = [];
            console.log('✓ Work connection lines cleared');
        }

        // Clear ApiDef visualization (ApiDef 클릭 시 생성된 화살표)
        this._clearApiDefVisualization(elementId);

        // Reset highlighted objects
        if (sceneData.highlightedObjects) {
            sceneData.highlightedObjects.forEach(obj => {
                if (obj.material && obj.material.emissive) {
                    // Restore original emissive if saved by _highlightDevice
                    if (obj.userData._origEmissiveHex !== undefined) {
                        obj.material.emissive.setHex(obj.userData._origEmissiveHex);
                        obj.material.emissiveIntensity = obj.userData._origEmissiveIntensity;
                        delete obj.userData._origEmissiveHex;
                        delete obj.userData._origEmissiveIntensity;
                    } else {
                        obj.material.emissiveIntensity = 0.3;
                    }
                }
            });
            sceneData.highlightedObjects = [];
        }
    },

    _showDetailPanel: function(elementId, workId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const workData = sceneData.config.works.find(w => w.id === workId);
        if (!workData) return;

        // Remove existing panel
        this._hideDetailPanel();

        // Create detail panel
        const panel = document.createElement('div');
        panel.id = 'ev2-detail-panel';
        panel.style.cssText = `
            position: absolute;
            bottom: 20px;
            left: 20px;
            background: rgba(15, 23, 42, 0.95);
            border: 1px solid rgba(71, 85, 105, 0.8);
            border-radius: 8px;
            padding: 16px;
            color: #e2e8f0;
            font-size: 12px;
            max-width: 350px;
            z-index: 100;
            backdrop-filter: blur(8px);
            box-shadow: 0 8px 32px rgba(0, 0, 0, 0.5);
        `;

        const stateColor = this.stateColors[workData.state] || this.stateColors.R;

        panel.innerHTML = `
            <div style="font-weight: 600; font-size: 14px; margin-bottom: 12px; color: #${stateColor.hex.toString(16).padStart(6, '0')};">
                ${workData.name || workData.id}
            </div>
            <div style="display: grid; grid-template-columns: auto 1fr; gap: 8px; font-size: 11px;">
                <span style="color: #94a3b8;">Flow:</span>
                <span>${workData.flowName || 'N/A'}</span>

                <span style="color: #94a3b8;">State:</span>
                <span style="color: #${stateColor.hex.toString(16).padStart(6, '0')};">${stateColor.name}</span>

                <span style="color: #94a3b8;">Progress:</span>
                <span>${workData.elapsed}s / ${workData.total}s</span>

                <span style="color: #94a3b8;">Calls:</span>
                <span>${workData.callCount || 0}</span>

                <span style="color: #94a3b8;">MT / WT:</span>
                <span>${workData.mt}s / ${workData.wt}s</span>

                <span style="color: #94a3b8;">Incoming:</span>
                <span style="color: #06b6d4;">${workData.incomingCount || 0} connections</span>

                <span style="color: #94a3b8;">Outgoing:</span>
                <span style="color: #fbbf24;">${workData.outgoingCount || 0} connections</span>

                <span style="color: #94a3b8;">Devices:</span>
                <span>${workData.devices ? workData.devices.join(', ') : 'None'}</span>
            </div>
            ${workData.calls && workData.calls.length > 0 ? `
                <div style="margin-top: 12px; padding-top: 12px; border-top: 1px solid rgba(71, 85, 105, 0.5);">
                    <div style="font-weight: 600; margin-bottom: 6px; font-size: 11px;">Calls:</div>
                    ${workData.calls.slice(0, 5).map((call, idx) => `
                        <div style="margin: 4px 0; padding: 4px; background: rgba(30, 41, 59, 0.5); border-radius: 4px; font-size: 10px;">
                            <span style="color: #${(this.stateColors[call.state] || this.stateColors.R).hex.toString(16).padStart(6, '0')};">
                                ${idx + 1}. ${call.state}
                            </span>
                            ${call.device ? `<span style="color: #94a3b8;"> | ${call.device}</span>` : ''}
                            ${call.activeTrigger ? `<span style="color: #fbbf24;"> | ⚡AT</span>` : ''}
                            ${call.autoCondi ? `<span style="color: #3b82f6;"> | 🔵AC</span>` : ''}
                            ${call.commonCondi ? `<span style="color: #8b5cf6;"> | 🟣CC</span>` : ''}
                        </div>
                    `).join('')}
                    ${workData.calls.length > 5 ? `<div style="color: #64748b; font-size: 10px; margin-top: 4px;">... and ${workData.calls.length - 5} more</div>` : ''}
                </div>
            ` : ''}
            <div style="margin-top: 12px; padding-top: 8px; border-top: 1px solid rgba(71, 85, 105, 0.5); font-size: 10px; color: #64748b;">
                Click elsewhere to deselect
            </div>
        `;

        sceneData.container.appendChild(panel);
    },

    _hideDetailPanel: function() {
        const panel = document.getElementById('ev2-detail-panel');
        if (panel) {
            panel.remove();
        }
    },

    /**
     * Visualize call chain connections (incoming/outgoing) in 3D
     */
    _visualizeCallChain: function(elementId, callId, callData, callCube) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Clear previous call chain visualization
        this._clearCallChainVisualization(elementId);

        // Initialize call chain array if not exists
        sceneData.callChainLines = sceneData.callChainLines || [];
        sceneData.callChainHighlights = sceneData.callChainHighlights || [];

        console.log(`Visualizing call chain for ${callId}`, callData);
        console.log(`  DEBUG: prev='${callData.prev}', next='${callData.next}', hasPrev=${callData.hasPrev}, hasNext=${callData.hasNext}`);
        console.log(`  DEBUG: incoming=[${callData.incoming?.join(',')||'empty'}], outgoing=[${callData.outgoing?.join(',')||'empty'}]`);

        // Get world position of clicked call cube
        const clickedPos = new THREE.Vector3();
        callCube.getWorldPosition(clickedPos);

        // Medium Priority Fix 3.3: Use cached call cubes instead of traversing scene
        const allCallCubes = Array.from(sceneData.callCubes.values());

        // Create a map of call identifiers to cube position
        // Support multiple keys: ID, ApiDefName, Device.ApiDefName
        const callPositions = {};
        allCallCubes.forEach(cube => {
            const pos = new THREE.Vector3();
            cube.getWorldPosition(pos);
            const callInfo = { cube, position: pos };
            const callData = cube.userData.callData;

            // Add by Call ID
            callPositions[cube.userData.callId] = callInfo;

            // Add by ApiDefName if available
            if (callData.apiDefName) {
                callPositions[callData.apiDefName] = callInfo;
            }

            // Add by Device.ApiDefName with multiple variants
            if (callData.device && callData.apiDefName) {
                // Full format: S151_BACK_PNL_PART_CT.ON
                const compositeKey = `${callData.device}.${callData.apiDefName}`;
                callPositions[compositeKey] = callInfo;

                // Without first segment: BACK_PNL_PART_CT.ON (remove S151_ prefix)
                const deviceParts = callData.device.split('_');
                if (deviceParts.length > 1) {
                    const deviceWithoutPrefix = deviceParts.slice(1).join('_');
                    const keyWithoutPrefix = `${deviceWithoutPrefix}.${callData.apiDefName}`;
                    callPositions[keyWithoutPrefix] = callInfo;
                    console.log(`  Mapped call: ${compositeKey} -> ${keyWithoutPrefix} (ID: ${cube.userData.callId})`);
                } else {
                    console.log(`  Mapped call: ${compositeKey} (ID: ${cube.userData.callId})`);
                }
            }
        });

        console.log(`Total call cubes: ${allCallCubes.length}, Total mappings: ${Object.keys(callPositions).length}`);

        // Highlight selected call
        if (callCube.userData.originalEmissive === undefined) {
            callCube.userData.originalEmissive = callCube.material.emissiveIntensity;
        }
        callCube.material.emissiveIntensity = 1.0;
        sceneData.callChainHighlights.push(callCube);

        // Determine incoming sources: use 'incoming' array if available, fallback to 'prev' field
        let incomingSources = [];
        if (callData.incoming && callData.incoming.length > 0) {
            incomingSources = callData.incoming;
            console.log('  Using incoming array from arrowcall table');
        } else if (callData.prev && callData.prev !== '') {
            // Fallback to prev field (call chain within work)
            incomingSources = [callData.prev];
            console.log('  Using prev field as fallback for incoming');
        }

        // Determine outgoing targets: use 'outgoing' array if available, fallback to 'next' field
        let outgoingTargets = [];
        if (callData.outgoing && callData.outgoing.length > 0) {
            outgoingTargets = callData.outgoing;
            console.log('  Using outgoing array from arrowcall table');
        } else if (callData.next && callData.next !== '') {
            // Fallback to next field (call chain within work)
            outgoingTargets = [callData.next];
            console.log('  Using next field as fallback for outgoing');
        }

        // Visualize incoming connections (cyan/turquoise)
        if (incomingSources.length > 0) {
            incomingSources.forEach(incomingId => {
                console.log(`  Looking for incoming call: ${incomingId}`);
                if (callPositions[incomingId]) {
                    const fromCube = callPositions[incomingId].cube;
                    const fromPos = callPositions[incomingId].position;

                    // Create arrow from incoming call to clicked call
                    const arrow = this._createCallArrow(fromPos, clickedPos, 0x06b6d4); // Cyan
                    sceneData.scene.add(arrow);
                    sceneData.callChainLines.push(arrow);

                    // Highlight incoming call
                    if (fromCube.userData.originalEmissive === undefined) {
                        fromCube.userData.originalEmissive = fromCube.material.emissiveIntensity;
                    }
                    fromCube.material.emissiveIntensity = 0.8;
                    sceneData.callChainHighlights.push(fromCube);

                    console.log(`  ✓ Incoming arrow created: ${incomingId} -> ${callId}`);
                } else {
                    console.log(`  ✗ Incoming call not found in scene: ${incomingId}`);
                }
            });
        }

        // Visualize outgoing connections (amber/yellow)
        if (outgoingTargets.length > 0) {
            outgoingTargets.forEach(outgoingId => {
                console.log(`  Looking for outgoing call: ${outgoingId}`);
                if (callPositions[outgoingId]) {
                    const toCube = callPositions[outgoingId].cube;
                    const toPos = callPositions[outgoingId].position;

                    // Create arrow from clicked call to outgoing call
                    const arrow = this._createCallArrow(clickedPos, toPos, 0xfbbf24); // Amber
                    sceneData.scene.add(arrow);
                    sceneData.callChainLines.push(arrow);

                    // Highlight outgoing call
                    if (toCube.userData.originalEmissive === undefined) {
                        toCube.userData.originalEmissive = toCube.material.emissiveIntensity;
                    }
                    toCube.material.emissiveIntensity = 0.8;
                    sceneData.callChainHighlights.push(toCube);

                    console.log(`  ✓ Outgoing arrow created: ${callId} -> ${outgoingId}`);
                } else {
                    console.log(`  ✗ Outgoing call not found in scene: ${outgoingId}`);
                }
            });
        }

        console.log(`Call chain visualization created: ${incomingSources.length} incoming, ${outgoingTargets.length} outgoing`);
    },

    /**
     * Create a 3D arrow between two call cubes
     */
    _createCallArrow: function(fromPos, toPos, color) {
        const group = new THREE.Group();

        // Create curved line
        const start = fromPos.clone();
        const end = toPos.clone();

        // Calculate control point for curve (elevated arc)
        const midpoint = new THREE.Vector3(
            (start.x + end.x) / 2,
            Math.max(start.y, end.y) + 1.5, // Elevate curve
            (start.z + end.z) / 2
        );

        const curve = new THREE.QuadraticBezierCurve3(start, midpoint, end);

        // Base tube line - thinner now since chevrons will show direction
        const tubeGeometry = new THREE.TubeGeometry(
            curve,
            30,      // tubularSegments
            0.04,    // radius - thinner base line
            8,       // radialSegments
            false
        );

        const tubeMaterial = new THREE.MeshStandardMaterial({
            color: color,
            emissive: color,
            emissiveIntensity: 0.3,
            transparent: true,
            opacity: 0.7,
            roughness: 0.3,
            metalness: 0.1
        });

        const tubeMesh = new THREE.Mesh(tubeGeometry, tubeMaterial);
        tubeMesh.castShadow = false;
        tubeMesh.receiveShadow = false;
        group.add(tubeMesh);

        // Add chevron markers along the curve (<<<<<<<<< style)
        const numChevrons = 8;  // Number of chevron markers
        const chevronGeometry = new THREE.ConeGeometry(0.15, 0.3, 4);  // Small cone for chevron
        const chevronMaterial = new THREE.MeshStandardMaterial({
            color: color,
            emissive: color,
            emissiveIntensity: 0.6,
            transparent: true,
            opacity: 0.9
        });

        // Place chevrons at regular intervals along the curve
        for (let i = 1; i <= numChevrons; i++) {
            const t = i / (numChevrons + 1);  // Position along curve (0 to 1)
            const point = curve.getPoint(t);

            // Get tangent at this point to determine direction
            const tangent = curve.getTangent(t).normalize();

            // Create chevron cone
            const chevron = new THREE.Mesh(chevronGeometry, chevronMaterial);
            chevron.position.copy(point);

            // Orient chevron to point in direction of travel
            chevron.quaternion.setFromUnitVectors(
                new THREE.Vector3(0, 1, 0),  // Cone's default direction
                tangent                       // Direction of curve at this point
            );

            chevron.castShadow = false;
            chevron.receiveShadow = false;
            group.add(chevron);
        }

        console.log(`✓ Created arrow with ${numChevrons} chevrons (<<<<<<< style) along curve`);
        return group;
    },

    /**
     * Clear call chain visualization
     */
    _clearCallChainVisualization: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Critical Fix 1.5: Remove call chain lines with recursive disposal
        if (sceneData.callChainLines) {
            sceneData.callChainLines.forEach(line => {
                sceneData.scene.remove(line);
                // Use recursive disposal to handle any children (e.g., chevrons)
                this._disposeSceneRecursive(line);
            });
            sceneData.callChainLines = [];
        }

        // Restore highlighted call cubes
        if (sceneData.callChainHighlights) {
            sceneData.callChainHighlights.forEach(cube => {
                if (cube.material && cube.userData.originalEmissive !== undefined) {
                    cube.material.emissiveIntensity = cube.userData.originalEmissive;
                }
            });
            sceneData.callChainHighlights = [];
        }

        console.log('Call chain visualization cleared and disposed');
    },

    /**
     * Clear all arrows from the scene (connection lines + call chain arrows)
     * @param {string} elementId - Scene identifier
     */
    clearAllArrows: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        console.log(`Clearing all arrows from scene '${elementId}'`);

        // Clear connection lines (Work 간 연결선)
        this._clearHighlights(elementId);

        // Clear call chain arrows (Call chain 화살표)
        this._clearCallChainVisualization(elementId);

        // Clear selection state
        sceneData.selectedObject = null;
        sceneData.selectedCall = null;

        console.log('✓ All arrows cleared');
    },

    /**
     * Create a single chevron (>) shape mesh pointing in +Z direction
     */
    _createChevronMesh: function(size, color) {
        // Create > shape using ConeGeometry - points in +Y by default
        const coneGeo = new THREE.ConeGeometry(size * 0.3, size * 0.6, 4);
        const coneMat = new THREE.MeshBasicMaterial({
            color: color,
            transparent: true,
            opacity: 0.95
        });
        const cone = new THREE.Mesh(coneGeo, coneMat);
        // Rotate so cone points in +Z direction (for easier lookAt usage)
        cone.rotation.x = Math.PI / 2;

        // Wrap in group so we can use lookAt on the group
        const group = new THREE.Group();
        group.add(cone);
        group.userData.material = coneMat; // Store reference for opacity changes

        return group;
    },

    /**
     * Create a parabolic/arc curve between two points
     * The arc goes UP first then comes DOWN to the target
     */
    _createArcCurve: function(startPos, endPos, arcHeight) {
        // Calculate midpoint and raise it for the arc peak
        const midPoint = new THREE.Vector3().lerpVectors(startPos, endPos, 0.5);
        midPoint.y += arcHeight; // Arc goes UP

        // Create quadratic bezier curve (parabola)
        const curve = new THREE.QuadraticBezierCurve3(
            startPos.clone(),
            midPoint,
            endPos.clone()
        );

        return curve;
    },

    /**
     * Create animated chevron path (>->->->) with PARABOLIC ARC between two points
     * Arrows flow from startPos, arc UP, then come DOWN to endPos (the ApiDef cube)
     */
    _createAnimatedChevronPath: function(startPos, endPos, color, sceneData, chevronCount = 5) {
        const pathGroup = new THREE.Group();
        const chevrons = [];

        // Calculate distance for arc height
        const distance = startPos.distanceTo(endPos);
        const arcHeight = Math.max(3, distance * 0.4); // Arc height proportional to distance

        // Create parabolic arc curve
        const curve = this._createArcCurve(startPos, endPos, arcHeight);
        const curvePoints = curve.getPoints(50); // 50 segments for smooth curve

        // Create THICK tube geometry for the path (visible arc line)
        const tubeGeo = new THREE.TubeGeometry(curve, 32, 0.08, 8, false);
        const tubeMat = new THREE.MeshBasicMaterial({
            color: color,
            transparent: true,
            opacity: 0.6
        });
        const tube = new THREE.Mesh(tubeGeo, tubeMat);
        pathGroup.add(tube);

        // Also add a glowing outer tube for effect
        const glowTubeGeo = new THREE.TubeGeometry(curve, 32, 0.15, 8, false);
        const glowTubeMat = new THREE.MeshBasicMaterial({
            color: color,
            transparent: true,
            opacity: 0.2
        });
        const glowTube = new THREE.Mesh(glowTubeGeo, glowTubeMat);
        pathGroup.add(glowTube);

        // Store curve for animation
        pathGroup.userData.curve = curve;

        // Create chevrons that will follow the arc path
        for (let i = 0; i < chevronCount; i++) {
            const chevron = this._createChevronMesh(0.7, color);
            chevron.userData.pathOffset = i / chevronCount; // 0 to 1, staggered start

            // Initial position at start of curve
            const t = chevron.userData.pathOffset;
            const pos = curve.getPoint(t);
            chevron.position.copy(pos);

            // Get tangent for orientation (direction along curve)
            const tangent = curve.getTangent(t);
            const lookTarget = pos.clone().add(tangent);
            chevron.lookAt(lookTarget);

            pathGroup.add(chevron);
            chevrons.push(chevron);
        }

        pathGroup.userData.chevrons = chevrons;
        pathGroup.userData.color = color;
        pathGroup.userData.startPos = startPos.clone();
        pathGroup.userData.endPos = endPos.clone();

        return pathGroup;
    },

    /**
     * Initialize ApiDef visualization state (minimal - just highlight cube)
     * Actual arrows are drawn by showApiDefConnections which is called from C# Handle3DApiDefSelection
     */
    _visualizeApiDefConnection: function(elementId, apiDefId, deviceId, clickedCube) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Clear previous ApiDef connections
        this._clearApiDefConnections(elementId);

        // Clear previous visualization
        this._clearApiDefVisualization(elementId);

        // Initialize storage
        if (!sceneData.apiDefVisualization) {
            sceneData.apiDefVisualization = { arrows: [], highlights: [], chevronPaths: [] };
        }

        // Highlight the clicked cube
        if (clickedCube.material) {
            clickedCube.userData.originalEmissive = clickedCube.material.emissiveIntensity;
            clickedCube.material.emissiveIntensity = 1.0;
            sceneData.apiDefVisualization.highlights.push(clickedCube);
        }

        // Get world position for logging
        const targetPos = new THREE.Vector3();
        clickedCube.getWorldPosition(targetPos);

        console.log(`ApiDef selected: ${apiDefId} on device ${deviceId} at (${targetPos.x.toFixed(2)}, ${targetPos.y.toFixed(2)}, ${targetPos.z.toFixed(2)})`);
        console.log('Waiting for C# Handle3DApiDefSelection to call showApiDefConnections...');

        // Note: C# Handle3DApiDefSelection will call showApiDefConnections with real connection data
        // This happens via the OnApiDefSelected callback above
    },

    /**
     * Create a label for connection source/target
     */
    _createConnectionLabel: function(text, type) {
        const canvas = document.createElement('canvas');
        canvas.width = 256;
        canvas.height = 64;
        const ctx = canvas.getContext('2d');

        // Background color based on type
        // work: cyan, apidef: orange, device: violet
        const bgColor = type === 'work' ? 'rgba(6, 182, 212, 0.8)' :
                        type === 'apidef' ? 'rgba(249, 115, 22, 0.8)' :
                        'rgba(139, 92, 246, 0.8)';
        ctx.fillStyle = bgColor;
        ctx.roundRect(0, 0, canvas.width, canvas.height, 8);
        ctx.fill();

        // Border
        ctx.strokeStyle = '#ffffff';
        ctx.lineWidth = 2;
        ctx.stroke();

        // Text
        ctx.fillStyle = '#ffffff';
        ctx.font = 'bold 24px Arial';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(text.substring(0, 20), canvas.width / 2, canvas.height / 2);

        const texture = new THREE.CanvasTexture(canvas);
        const spriteMat = new THREE.SpriteMaterial({ map: texture, transparent: true });
        const sprite = new THREE.Sprite(spriteMat);
        sprite.scale.set(3, 0.75, 1);

        return sprite;
    },

    /**
     * Clear ApiDef visualization
     */
    _clearApiDefVisualization: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        if (sceneData.apiDefVisualization) {
            // Remove arrows and labels
            sceneData.apiDefVisualization.arrows.forEach(obj => {
                sceneData.scene.remove(obj);
                if (obj.geometry) obj.geometry.dispose();
                if (obj.material) {
                    if (Array.isArray(obj.material)) {
                        obj.material.forEach(m => m.dispose());
                    } else {
                        obj.material.dispose();
                    }
                }
            });
            sceneData.apiDefVisualization.arrows = [];

            // Remove chevron paths
            if (sceneData.apiDefVisualization.chevronPaths) {
                sceneData.apiDefVisualization.chevronPaths.forEach(pathGroup => {
                    // Dispose chevrons
                    if (pathGroup.userData.chevrons) {
                        pathGroup.userData.chevrons.forEach(chevron => {
                            if (chevron.geometry) chevron.geometry.dispose();
                            if (chevron.material) chevron.material.dispose();
                        });
                    }
                    // Dispose path line
                    pathGroup.traverse(child => {
                        if (child.geometry) child.geometry.dispose();
                        if (child.material) child.material.dispose();
                    });
                    sceneData.scene.remove(pathGroup);
                });
                sceneData.apiDefVisualization.chevronPaths = [];
            }

            // Restore highlights
            sceneData.apiDefVisualization.highlights.forEach(cube => {
                if (cube.material && cube.userData.originalEmissive !== undefined) {
                    cube.material.emissiveIntensity = cube.userData.originalEmissive;
                }
            });
            sceneData.apiDefVisualization.highlights = [];
        }

        // NOTE: Do NOT set selectedApiDef = null here!
        // It should only be cleared when explicitly deselecting (toggling off).
        // Otherwise, the animation will stop immediately when re-creating visualization.
        console.log('ApiDef visualization cleared');
    },

    /**
     * Draw arrows from actual callers (Works) to the ApiDef cube
     * Called from Blazor with Call table data
     * @param {string} elementId - Scene ID
     * @param {string} apiDefId - Target ApiDef ID
     * @param {Array} callers - Array of {callId, workName, flowName, state}
     */
    drawApiDefCallerArrows: function(elementId, apiDefId, callers) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn('Scene not found:', elementId);
            return;
        }

        console.log(`=== drawApiDefCallerArrows ===`);
        console.log(`apiDefId: ${apiDefId}`);
        console.log(`callers count: ${callers.length}`);
        console.log(`callers data:`, JSON.stringify(callers, null, 2));

        // Find the ApiDef cube by ID
        let targetCube = null;
        let targetDeviceId = null;
        sceneData.scene.traverse(obj => {
            if (obj.userData && obj.userData.apiDefId === apiDefId) {
                targetCube = obj;
                targetDeviceId = obj.userData.deviceId;
            }
        });

        if (!targetCube) {
            console.warn(`ApiDef cube not found: ${apiDefId}`);
            // List all ApiDef cubes in scene for debugging
            console.log('Available ApiDef cubes in scene:');
            sceneData.scene.traverse(obj => {
                if (obj.userData && obj.userData.apiDefId) {
                    console.log(`  - ${obj.userData.apiDefId} (device: ${obj.userData.deviceId})`);
                }
            });
            return;
        }

        console.log(`Target ApiDef found on device: ${targetDeviceId}`);

        // Clear previous visualization
        this._clearApiDefVisualization(elementId);

        // Get target position (ApiDef cube)
        const targetPos = new THREE.Vector3();
        targetCube.getWorldPosition(targetPos);

        console.log(`Target ApiDef position: (${targetPos.x.toFixed(2)}, ${targetPos.y.toFixed(2)}, ${targetPos.z.toFixed(2)})`);

        // Initialize visualization storage
        sceneData.apiDefVisualization = { arrows: [], highlights: [], chevronPaths: [] };
        sceneData.selectedApiDef = apiDefId;

        // Highlight the cube
        if (targetCube.material) {
            targetCube.userData.originalEmissive = targetCube.material.emissiveIntensity;
            targetCube.material.emissiveIntensity = 1.0;
            sceneData.apiDefVisualization.highlights.push(targetCube);
        }

        // Create pulsing ring at target
        const ringGeo = new THREE.RingGeometry(0.5, 0.7, 32);
        const ringMat = new THREE.MeshBasicMaterial({
            color: 0x06b6d4,
            side: THREE.DoubleSide,
            transparent: true,
            opacity: 0.8
        });
        const ring = new THREE.Mesh(ringGeo, ringMat);
        ring.position.copy(targetPos);
        ring.rotation.x = -Math.PI / 2;
        sceneData.scene.add(ring);
        sceneData.apiDefVisualization.arrows.push(ring);

        // Find caller positions and draw arrows
        const arrowColor = 0x06b6d4;
        const callerPositions = [];

        callers.forEach((caller, idx) => {
            console.log(`Processing caller ${idx}: device=${caller.device}, workName=${caller.workName}, flowName=${caller.flowName}`);

            // Skip if caller device is same as target device (self-reference)
            if (caller.device && caller.device === targetDeviceId) {
                console.log(`  → Skipping self-reference (same device: ${caller.device})`);
                return;
            }

            // Try to find position: first by device, then by workName
            let callerPos = null;
            let callerName = caller.workName || caller.device || 'Unknown';

            // First try to find by device name (for device-based 3D view)
            if (caller.device) {
                console.log(`  → Searching for device: ${caller.device}`);
                callerPos = this._findDevicePosition(sceneData, caller.device);
                if (callerPos) {
                    callerName = caller.device;
                    console.log(`  → Found device position: (${callerPos.x.toFixed(2)}, ${callerPos.y.toFixed(2)}, ${callerPos.z.toFixed(2)})`);
                }
            }

            // If not found by device, try by work name
            if (!callerPos && caller.workName) {
                console.log(`  → Searching for work: ${caller.workName}`);
                callerPos = this._findWorkPosition(sceneData, caller.workName, caller.flowName);
                if (callerPos) {
                    callerName = caller.workName;
                    console.log(`  → Found work position: (${callerPos.x.toFixed(2)}, ${callerPos.y.toFixed(2)}, ${callerPos.z.toFixed(2)})`);
                }
            }

            if (callerPos) {
                callerPositions.push({
                    pos: callerPos,
                    name: callerName,
                    callId: caller.callId,
                    state: caller.state
                });
            } else {
                console.log(`  → Position not found for caller`);
            }
        });

        console.log(`Found ${callerPositions.length} caller positions out of ${callers.length} callers`);

        // Draw arc arrows from each caller to target
        callerPositions.forEach((caller, idx) => {
            console.log(`Creating arrow ${idx}: from (${caller.pos.x.toFixed(2)}, ${caller.pos.y.toFixed(2)}, ${caller.pos.z.toFixed(2)}) to (${targetPos.x.toFixed(2)}, ${targetPos.y.toFixed(2)}, ${targetPos.z.toFixed(2)})`);

            const chevronPath = this._createAnimatedChevronPath(
                caller.pos,
                targetPos,
                arrowColor,
                sceneData,
                5
            );

            if (chevronPath) {
                console.log(`  ✓ Chevron path created, children: ${chevronPath.children.length}, userData.chevrons: ${chevronPath.userData.chevrons?.length || 0}`);
                sceneData.scene.add(chevronPath);
                sceneData.apiDefVisualization.chevronPaths.push(chevronPath);
            } else {
                console.error(`  ✗ Failed to create chevron path for caller ${idx}`);
            }

            // Add caller label
            const label = this._createConnectionLabel(caller.name, 'work');
            label.position.copy(caller.pos);
            label.position.y += 1.5;
            sceneData.scene.add(label);
            sceneData.apiDefVisualization.arrows.push(label);
            console.log(`  ✓ Label created for '${caller.name}'`);
        });

        // Start animation for arrows
        this._animateApiDefCallerArrows(elementId, apiDefId, ring);

        console.log(`✓ ApiDef caller arrows created: ${callerPositions.length} arrows`);
    },

    /**
     * Draw outgoing arrows from ApiDef cube to target devices
     * Draw connection arrows based on Call.next/prev fields
     * @param {string} elementId - Scene ID
     * @param {string} apiDefId - Source ApiDef ID (e.g., "S141_SOL_S141_LH_L_SRV_MOVE.START")
     * @param {string} deviceId - Source Device ID
     * @param {Array} outgoing - next 필드 기반 outgoing calls (이후) - Array of {callId, device, apiDefName, state}
     * @param {Array} incoming - prev 필드 기반 incoming calls (이전) - Array of {callId, device, apiDefName, state}
     */
    drawApiDefConnectionArrows: function(elementId, apiDefId, deviceId, outgoing, incoming) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn('Scene not found:', elementId);
            return;
        }

        console.log(`=== drawApiDefConnectionArrows ===`);
        console.log(`apiDefId: ${apiDefId}, deviceId: ${deviceId}`);
        console.log(`outgoing (next): ${outgoing.length}, incoming (prev): ${incoming.length}`);

        // Find the source ApiDef cube by ID
        let sourceCube = null;
        sceneData.scene.traverse(obj => {
            if (obj.userData && obj.userData.apiDefId === apiDefId) {
                sourceCube = obj;
            }
        });

        if (!sourceCube) {
            console.warn(`Source ApiDef cube not found: ${apiDefId}`);
            return;
        }

        // Clear previous visualization
        this._clearApiDefVisualization(elementId);

        // Get source position (clicked ApiDef cube)
        const sourcePos = new THREE.Vector3();
        sourceCube.getWorldPosition(sourcePos);

        console.log(`Source ApiDef position: (${sourcePos.x.toFixed(2)}, ${sourcePos.y.toFixed(2)}, ${sourcePos.z.toFixed(2)})`);

        // Initialize visualization storage
        sceneData.apiDefVisualization = { arrows: [], highlights: [], chevronPaths: [] };
        sceneData.selectedApiDef = apiDefId;

        // Highlight the source cube
        if (sourceCube.material) {
            sourceCube.userData.originalEmissive = sourceCube.material.emissiveIntensity;
            sourceCube.material.emissiveIntensity = 1.0;
            sceneData.apiDefVisualization.highlights.push(sourceCube);
        }

        // Create pulsing ring at source (green color)
        const ringGeo = new THREE.RingGeometry(0.5, 0.7, 32);
        const ringMat = new THREE.MeshBasicMaterial({
            color: 0x10b981,  // Green for source
            side: THREE.DoubleSide,
            transparent: true,
            opacity: 0.8
        });
        const ring = new THREE.Mesh(ringGeo, ringMat);
        ring.position.copy(sourcePos);
        ring.rotation.x = -Math.PI / 2;
        sceneData.scene.add(ring);
        sceneData.apiDefVisualization.arrows.push(ring);

        const outgoingColor = 0xf97316;  // Orange for outgoing (이후)
        const incomingColor = 0x06b6d4;  // Cyan for incoming (이전)

        // ====== OUTGOING (next 필드 기반, 이후) ======
        console.log(`--- Processing ${outgoing.length} outgoing targets ---`);
        outgoing.forEach((target, idx) => {
            // Skip if target device is same as source device (self-reference)
            if (target.device && target.device === deviceId) {
                console.log(`  Outgoing ${idx}: Skipping self-reference (same device: ${target.device})`);
                return;
            }

            const targetPos = this._findDevicePosition(sceneData, target.device);
            if (targetPos) {
                console.log(`  Outgoing ${idx}: ${deviceId} → ${target.device}`);

                // Draw chevron path from source to target
                const chevronPath = this._createAnimatedChevronPath(
                    sourcePos,      // FROM: clicked ApiDef
                    targetPos,      // TO: outgoing target device
                    outgoingColor,
                    sceneData,
                    5
                );

                if (chevronPath) {
                    sceneData.scene.add(chevronPath);
                    sceneData.apiDefVisualization.chevronPaths.push(chevronPath);
                }

                // Add label at target
                const label = this._createConnectionLabel(`→ ${target.device}.${target.apiDefName || ''}`, 'apidef');
                label.position.copy(targetPos);
                label.position.y += 1.5;
                sceneData.scene.add(label);
                sceneData.apiDefVisualization.arrows.push(label);
            } else {
                console.log(`  Outgoing ${idx}: Position not found for device '${target.device}'`);
            }
        });

        // ====== INCOMING (prev 필드 기반, 이전) ======
        console.log(`--- Processing ${incoming.length} incoming sources ---`);
        incoming.forEach((source, idx) => {
            // Skip if source device is same as current device (self-reference)
            if (source.device && source.device === deviceId) {
                console.log(`  Incoming ${idx}: Skipping self-reference (same device: ${source.device})`);
                return;
            }

            const incomingPos = this._findDevicePosition(sceneData, source.device);
            if (incomingPos) {
                console.log(`  Incoming ${idx}: ${source.device} → ${deviceId}`);

                // Draw chevron path from incoming source to current position
                const chevronPath = this._createAnimatedChevronPath(
                    incomingPos,    // FROM: incoming source device
                    sourcePos,      // TO: clicked ApiDef
                    incomingColor,
                    sceneData,
                    5
                );

                if (chevronPath) {
                    sceneData.scene.add(chevronPath);
                    sceneData.apiDefVisualization.chevronPaths.push(chevronPath);
                }

                // Add label at source
                const label = this._createConnectionLabel(`← ${source.device}.${source.apiDefName || ''}`, 'work');
                label.position.copy(incomingPos);
                label.position.y += 1.5;
                sceneData.scene.add(label);
                sceneData.apiDefVisualization.arrows.push(label);
            } else {
                console.log(`  Incoming ${idx}: Position not found for device '${source.device}'`);
            }
        });

        // Start animation for arrows
        this._animateApiDefCallerArrows(elementId, apiDefId, ring);

        console.log(`✓ Connection arrows created: ${outgoing.length} outgoing, ${incoming.length} incoming`);
    },

    /**
     * Find Work position in scene by workName
     * @param {object} sceneData - Scene data
     * @param {string} workName - Work name to find
     * @param {string} flowName - Flow name (for disambiguation)
     * @returns {THREE.Vector3|null} World position or null if not found
     */
    _findWorkPosition: function(sceneData, workName, flowName) {
        // Search in stationMeshes (Work-based entities)
        if (sceneData.stationMeshes) {
            for (const [workId, mesh] of Object.entries(sceneData.stationMeshes)) {
                const workData = mesh.userData.workData;
                if (workData && workData.name === workName) {
                    const group = mesh.parent;
                    if (group) {
                        const worldPos = new THREE.Vector3();
                        group.getWorldPosition(worldPos);
                        worldPos.y += 2; // Above the work station
                        console.log(`Found Work '${workName}' at (${worldPos.x.toFixed(2)}, ${worldPos.y.toFixed(2)}, ${worldPos.z.toFixed(2)})`);
                        return worldPos;
                    }
                }
            }
        }

        // Search in deviceMeshes (Device-based entities) - in case Work is represented as device
        if (sceneData.deviceMeshes) {
            for (const [deviceId, mesh] of Object.entries(sceneData.deviceMeshes)) {
                const deviceData = mesh.userData.deviceData;
                if (deviceData && deviceData.name === workName) {
                    const group = mesh.parent;
                    if (group) {
                        const worldPos = new THREE.Vector3();
                        group.getWorldPosition(worldPos);
                        worldPos.y += 2;
                        console.log(`Found Device as Work '${workName}' at (${worldPos.x.toFixed(2)}, ${worldPos.y.toFixed(2)}, ${worldPos.z.toFixed(2)})`);
                        return worldPos;
                    }
                }
            }
        }

        console.warn(`Work position not found: ${workName} (${flowName})`);
        return null;
    },

    /**
     * Find Device position in scene by deviceName
     * @param {object} sceneData - Scene data
     * @param {string} deviceName - Device name to find
     * @returns {THREE.Vector3|null} World position or null if not found
     */
    _findDevicePosition: function(sceneData, deviceName) {
        if (!deviceName) return null;

        console.log(`_findDevicePosition: searching for '${deviceName}'`);
        console.log(`  deviceMeshes keys: ${sceneData.deviceMeshes ? Object.keys(sceneData.deviceMeshes).join(', ') : 'none'}`);
        console.log(`  devices array: ${sceneData.devices ? sceneData.devices.map(d => d.name || d.id).join(', ') : 'none'}`);

        // Search in deviceMeshes
        if (sceneData.deviceMeshes) {
            for (const [deviceId, mesh] of Object.entries(sceneData.deviceMeshes)) {
                // Check by device ID
                if (deviceId === deviceName) {
                    const group = mesh.parent;
                    if (group) {
                        const worldPos = new THREE.Vector3();
                        group.getWorldPosition(worldPos);
                        worldPos.y += 2; // Above the device
                        console.log(`Found Device '${deviceName}' by ID at (${worldPos.x.toFixed(2)}, ${worldPos.y.toFixed(2)}, ${worldPos.z.toFixed(2)})`);
                        return worldPos;
                    }
                }

                // Check by device name in userData
                const deviceData = mesh.userData.deviceData || (mesh.parent && mesh.parent.userData.deviceData);
                if (deviceData && deviceData.name === deviceName) {
                    const group = mesh.parent;
                    if (group) {
                        const worldPos = new THREE.Vector3();
                        group.getWorldPosition(worldPos);
                        worldPos.y += 2;
                        console.log(`Found Device '${deviceName}' by name at (${worldPos.x.toFixed(2)}, ${worldPos.y.toFixed(2)}, ${worldPos.z.toFixed(2)})`);
                        return worldPos;
                    }
                }
            }
        }

        // Also search in sceneData.devices array
        if (sceneData.devices) {
            const device = sceneData.devices.find(d => d.name === deviceName || d.id === deviceName);
            if (device && device.posX !== undefined && device.posZ !== undefined) {
                const worldPos = new THREE.Vector3(device.posX, 2, device.posZ);
                console.log(`Found Device '${deviceName}' in devices array at (${worldPos.x.toFixed(2)}, ${worldPos.y.toFixed(2)}, ${worldPos.z.toFixed(2)})`);
                return worldPos;
            }
        }

        console.warn(`Device position not found: ${deviceName}`);
        return null;
    },

    /**
     * Animate ApiDef caller arrows (pulsing ring and chevron paths)
     * @param {string} elementId - Scene ID
     * @param {string} apiDefId - Target ApiDef ID
     * @param {THREE.Mesh} ring - Pulsing ring mesh
     */
    _animateApiDefCallerArrows: function(elementId, apiDefId, ring) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const animate = () => {
            if (!sceneData.apiDefVisualization || sceneData.selectedApiDef !== apiDefId) return;

            const time = Date.now() * 0.001;
            const speed = 0.35;

            // Animate pulsing ring
            const pulseScale = 1 + Math.sin(time * 3) * 0.25;
            ring.scale.set(pulseScale, pulseScale, 1);
            ring.material.opacity = 0.6 + Math.sin(time * 3) * 0.35;

            // Animate each chevron path along the arc curve
            sceneData.apiDefVisualization.chevronPaths.forEach(pathGroup => {
                if (!pathGroup.userData.chevrons || !pathGroup.userData.curve) return;

                const curve = pathGroup.userData.curve;

                pathGroup.userData.chevrons.forEach((chevron, idx) => {
                    let t = ((time * speed + chevron.userData.pathOffset) % 1);
                    const pos = curve.getPoint(t);
                    chevron.position.copy(pos);

                    const tangent = curve.getTangent(t);
                    const lookTarget = pos.clone().add(tangent);
                    chevron.lookAt(lookTarget);

                    let opacity = 1.0;
                    if (t < 0.1) opacity = t / 0.1;
                    else if (t > 0.9) opacity = (1 - t) / 0.1;

                    if (chevron.userData.material) {
                        chevron.userData.material.opacity = opacity * 0.95;
                    }

                    const pulse = 1 + Math.sin(time * 4 + idx * 0.7) * 0.2;
                    chevron.scale.set(pulse, pulse, pulse);
                });
            });

            requestAnimationFrame(animate);
        };
        animate();
    },

    /**
     * Update call cubes cache after dynamically adding works/devices
     * This must be called after placeAllWorks/placeAllDevices to enable call chain visualization
     * @param {string} elementId - Scene identifier
     */
    _updateCallCubesCache: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Clear existing cache
        if (!sceneData.callCubes) {
            sceneData.callCubes = new Map();
        } else {
            sceneData.callCubes.clear();
        }

        // Traverse scene and collect all call cubes
        sceneData.scene.traverse((obj) => {
            if (obj.userData && obj.userData.callId && obj.userData.isCallBox) {
                sceneData.callCubes.set(obj.userData.callId, obj);
            }
        });

        console.log(`✓ Updated call cubes cache: ${sceneData.callCubes.size} cubes`);
    },

    // ============================================
    // Device-based 3D View (Device as primary layout unit)
    // ============================================

    /**
     * Initialize Device-based 3D scene
     * @param {string} elementId - Container DOM id
     * @param {object} config - Device config { devices, connections, workZones }
     * @returns {boolean} Success status
     */
    initDeviceView: function(elementId, config = {}) {
        const container = document.getElementById(elementId);
        if (!container) {
            console.error(`Container element '${elementId}' not found`);
            return false;
        }

        if (typeof THREE === 'undefined') {
            console.error('Three.js is not loaded');
            return false;
        }

        try {
            console.log('=== Ev23DViewer.initDeviceView ===');
            console.log('Devices count:', config.devices ? config.devices.length : 0);
            console.log('Connections count:', config.connections ? config.connections.length : 0);
            console.log('Work zones count:', config.workZones ? config.workZones.length : 0);

            if (!config.devices || config.devices.length === 0) {
                console.warn('⚠️ No devices data received! Cannot render 3D scene.');
                return false;
            }

            // Scene setup
            const scene = new THREE.Scene();
            scene.background = new THREE.Color(0x0f172a);
            scene.fog = new THREE.Fog(0x0f172a, 100, 300);

            // Camera
            const camera = new THREE.PerspectiveCamera(
                50,
                container.clientWidth / container.clientHeight,
                0.1,
                1000
            );
            camera.position.set(0, 20, 40);
            camera.lookAt(0, 0, 0);

            // Renderer
            const renderer = new THREE.WebGLRenderer({ antialias: true });
            renderer.setSize(container.clientWidth, container.clientHeight);
            renderer.shadowMap.enabled = true;
            renderer.shadowMap.type = THREE.PCFSoftShadowMap;
            container.appendChild(renderer.domElement);

            // OrbitControls
            const controls = new THREE.OrbitControls(camera, renderer.domElement);
            controls.enableDamping = true;
            controls.dampingFactor = 0.05;
            controls.maxPolarAngle = Math.PI / 2;
            controls.minDistance = 10;
            controls.maxDistance = 300;
            controls.target.set(0, 0, 0);
            controls.mouseButtons = {
                LEFT: THREE.MOUSE.ROTATE,
                MIDDLE: THREE.MOUSE.PAN,
                RIGHT: THREE.MOUSE.PAN
            };
            controls.update();

            // Lighting
            this._setupLighting(scene);

            // Load saved device positions from localStorage
            const savedPositions = this._loadDevicePositions(elementId);
            if (savedPositions && Object.keys(savedPositions).length > 0) {
                console.log(`✓ Loaded ${Object.keys(savedPositions).length} saved device positions`);
                config.devices.forEach(device => {
                    if (savedPositions[device.id]) {
                        device.posX = savedPositions[device.id].x;
                        device.posZ = savedPositions[device.id].z;
                    }
                });
            }

            const devices = config.devices || [];
            const connections = config.connections || [];
            const workZones = config.workZones || [];

            // Create device-based scene
            const deviceMeshes = this._createDeviceScene(scene, devices, workZones);

            // Create device connections
            this._createDeviceConnections(scene, connections, deviceMeshes);

            // Auto-scale camera
            if (devices.length > 0) {
                this._fitCameraToDevices(scene, camera, controls, devices);
            }

            // Animation loop
            let animationId;
            let time = 0;
            const animate = () => {
                try {
                    animationId = requestAnimationFrame(animate);
                    time += 0.016;

                    // Animate ApiDef cubes (gentle pulse), flow particles, and Lib3D device motions
                    scene.traverse((obj) => {
                        if (obj.userData && obj.userData.isApiDefCube) {
                            const pulse = 0.9 + Math.sin(time * 2 + obj.userData.pulseOffset) * 0.1;
                            obj.scale.setScalar(pulse);
                        }

                        // Lib3D motion animation (apiDef Going state → animate toward target)
                        if (obj.userData && obj.userData.isDeviceIndicator && obj.userData.activeAnimation) {
                            if (window.Ds2View3DLibrary) {
                                window.Ds2View3DLibrary.animate(obj, obj.userData.activeAnimation, 0.08);
                            }
                        }

                        // Animate ApiDef arrow flow particles
                        if (obj.userData && obj.userData.isFlowParticle) {
                            const points = obj.userData.curvePoints;
                            if (points && points.length > 0) {
                                // Update progress
                                obj.userData.progress += (obj.userData.reversed ? -0.005 : 0.005);
                                if (obj.userData.progress > 1) obj.userData.progress = 0;
                                if (obj.userData.progress < 0) obj.userData.progress = 1;

                                // Get position along curve
                                const index = Math.floor(obj.userData.progress * (points.length - 1));
                                obj.position.copy(points[index]);
                            }
                        }
                    });

                    controls.update();
                    renderer.render(scene, camera);
                } catch (err) {
                    console.error('Animation error:', err);
                    cancelAnimationFrame(animationId);
                }
            };
            animate();

            // Collect call cubes for caching
            const callCubes = new Map();
            scene.traverse((obj) => {
                if (obj.userData && obj.userData.callId && obj.userData.isCallBox) {
                    callCubes.set(obj.userData.callId, obj);
                }
            });
            console.log(`✓ Cached ${callCubes.size} call cubes for fast lookup`);

            // Store scene data
            this._scenes[elementId] = {
                scene,
                camera,
                renderer,
                controls,
                deviceMeshes,
                stationMeshes: deviceMeshes, // Alias for compatibility
                animationId,
                config: {},
                selectedObject: null,
                selectedCall: null,
                hoveredObject: null,
                connectionLines: [],
                highlightedObjects: [],
                callSelectionCallback: null,
                editMode: false,  // Device edit mode (drag & drop)
                reusableVector2: new THREE.Vector2(),
                reusableVector3: new THREE.Vector3(),
                reusableRaycaster: new THREE.Raycaster(),
                callCubes: callCubes,
                callChainLines: [],
                callChainHighlights: [],
                workZones: {},
                devices: devices,
                // Flow Zone auto-recalculation debounce timer
                flowRecalcTimer: null,
                // Drag state for device movement
                dragState: {
                    isDragging: false,
                    dragObject: null,
                    workId: null,
                    deviceId: null,
                    dragPlane: new THREE.Plane(),
                    dragOffset: new THREE.Vector3(),
                    mouseDownPos: new THREE.Vector2(),
                    mouseMoveThreshold: 5
                }
            };

            // Setup event handlers (use unified handler for both Work and Device scenes)
            this._setupInteractionHandlers(elementId);

            // Resize handler
            const resizeObserver = new ResizeObserver(() => {
                const width = container.clientWidth;
                const height = container.clientHeight;
                camera.aspect = width / height;
                camera.updateProjectionMatrix();
                renderer.setSize(width, height);
            });
            resizeObserver.observe(container);
            this._scenes[elementId].resizeObserver = resizeObserver;

            console.log(`✓ Device 3D scene initialized for '${elementId}' with ${devices.length} devices`);
            return true;
        } catch (error) {
            console.error('Failed to initialize device 3D scene:', error);
            return false;
        }
    },

    /**
     * Create device-based factory scene
     */
    _createDeviceScene: function(scene, devices, workZones) {
        // Calculate floor size based on device positions (auto-expand)
        let minX = Infinity, maxX = -Infinity;
        let minZ = Infinity, maxZ = -Infinity;

        devices.forEach(d => {
            const x = d.posX || 0;
            const z = d.posZ || 0;
            minX = Math.min(minX, x);
            maxX = Math.max(maxX, x);
            minZ = Math.min(minZ, z);
            maxZ = Math.max(maxZ, z);
        });

        // Calculate floor size with padding
        const padding = 40;
        const width = maxX - minX + padding * 2;
        const depth = maxZ - minZ + padding * 2;
        const floorSize = Math.max(width, depth, 200);  // Minimum 200

        // Calculate floor center offset
        const floorCenterX = (minX + maxX) / 2;
        const floorCenterZ = (minZ + maxZ) / 2;

        const gridDivisions = Math.max(Math.round(floorSize / 5), 40);

        // Ground plane - centered on device layout
        const groundGeo = new THREE.PlaneGeometry(floorSize, floorSize);
        const groundMat = new THREE.MeshStandardMaterial({
            color: 0x1e293b,
            roughness: 0.9,
            metalness: 0.1
        });
        const ground = new THREE.Mesh(groundGeo, groundMat);
        ground.rotation.x = -Math.PI / 2;
        ground.position.set(floorCenterX, -0.1, floorCenterZ);
        ground.receiveShadow = true;
        ground.name = 'ground';
        ground.userData.isFloor = true;
        scene.add(ground);

        // Grid - centered on device layout
        const gridHelper = new THREE.GridHelper(floorSize, gridDivisions, 0x334155, 0x1e293b);
        gridHelper.position.set(floorCenterX, 0, floorCenterZ);
        scene.add(gridHelper);

        console.log(`✓ Dynamic floor created: ${floorSize.toFixed(0)}x${floorSize.toFixed(0)} centered at (${floorCenterX.toFixed(1)}, ${floorCenterZ.toFixed(1)})`);
        console.log(`  Device bounds: X[${minX.toFixed(1)}, ${maxX.toFixed(1)}] Z[${minZ.toFixed(1)}, ${maxZ.toFixed(1)}]`);

        // Work Zones (floor rectangles)
        this._createWorkZonesOnFloor(scene, workZones, devices);

        // Device stations
        const deviceMeshes = {};
        devices.forEach((device, idx) => {
            const station = this._createDeviceStation(device, idx, scene);
            scene.add(station.group);
            deviceMeshes[device.id] = station.group;  // Store group (contains ApiDef cubes), not just indicator
        });

        return deviceMeshes;
    },

    /**
     * Create Work Zones on floor
     */
    _createWorkZonesOnFloor: function(scene, workZones, devices) {
        if (!workZones || workZones.length === 0) return;

        workZones.forEach((zone, idx) => {
            // Find devices in this zone
            const zoneDevices = devices.filter(d => zone.deviceIds.includes(d.id));
            if (zoneDevices.length === 0) return;

            // Calculate bounding box
            let minX = Infinity, maxX = -Infinity;
            let minZ = Infinity, maxZ = -Infinity;

            zoneDevices.forEach(d => {
                minX = Math.min(minX, d.posX);
                maxX = Math.max(maxX, d.posX);
                minZ = Math.min(minZ, d.posZ);
                maxZ = Math.max(maxZ, d.posZ);
            });

            const padding = 4;
            const width = Math.max(maxX - minX + padding * 2, 8);
            const depth = Math.max(maxZ - minZ + padding * 2, 8);
            const centerX = (minX + maxX) / 2;
            const centerZ = (minZ + maxZ) / 2;

            // Parse color
            let color = 0x10b981;
            if (zone.color) {
                if (typeof zone.color === 'string' && zone.color.startsWith('#')) {
                    color = parseInt(zone.color.slice(1), 16);
                } else if (typeof zone.color === 'number') {
                    color = zone.color;
                }
            }

            // Zone rectangle
            const zoneGeo = new THREE.PlaneGeometry(width, depth);
            const zoneMat = new THREE.MeshStandardMaterial({
                color: color,
                transparent: true,
                opacity: 0.15,
                side: THREE.DoubleSide
            });
            const zoneMesh = new THREE.Mesh(zoneGeo, zoneMat);
            zoneMesh.rotation.x = -Math.PI / 2;
            zoneMesh.position.set(centerX, 0.02, centerZ);
            zoneMesh.userData.workZoneId = zone.workId;
            zoneMesh.userData.isWorkZone = true;
            scene.add(zoneMesh);

            // Zone label
            if (zone.workName) {
                const label = this._createLabel(zone.workName, 0.8);
                label.position.set(centerX, 0.1, centerZ - depth / 2 - 1);
                scene.add(label);
            }

            console.log(`✓ Created work zone: ${zone.workName} at (${centerX.toFixed(1)}, ${centerZ.toFixed(1)})`);
        });
    },

    /**
     * Create individual device station with ApiDef cubes
     * @param {object} device - Device data
     * @param {number} index - Device index
     * @param {THREE.Scene} scene - Scene for Flow Zone color lookup
     */
    _createDeviceStation: function(device, index, scene = null) {
        const group = new THREE.Group();
        group.position.set(device.posX || 0, device.posY || 0, device.posZ || 0);
        group.userData.deviceId = device.id;
        group.userData.deviceData = device;

        const stateColor = this.stateColors[device.state] || this.stateColors.R;

        // Get Flow Zone color for this device
        const flowColor = scene && device.flowName
            ? this._getFlowZoneColor(scene, device.flowName)
            : null;

        // Infer deviceType from name if not specified or is 'general'
        let effectiveDeviceType = device.deviceType || 'general';
        if (effectiveDeviceType === 'general' && device.name) {
            const upperName = device.name.toUpperCase();
            // Check for robot patterns: RB, ROBOT
            if (/_RB\d*$/.test(upperName) || /RB\d+/.test(upperName) || upperName.includes('ROBOT')) {
                effectiveDeviceType = 'robot';
            }
            // Check for cylinder/small patterns: CLP, CT, SV, AIR, SLIDE, LOCK
            else if (/_(CLP|CT|SV|AIR)(_|$)/.test(upperName) ||
                     upperName.includes('SLIDE') ||
                     upperName.includes('LOCK') ||
                     upperName.includes('CYLINDER')) {
                effectiveDeviceType = 'small';
            }
        }

        // Create device model based on type
        let model;
        let modelHeight = 2;
        let isRobotType = false;
        if (effectiveDeviceType === 'robot') {
            model = this._createRobotModel(1.0, stateColor);
            modelHeight = 3;
            isRobotType = true;
        } else if (effectiveDeviceType === 'small' || effectiveDeviceType === 'cylinder') {
            model = this._createSmallDeviceModel(1.0, stateColor);
            modelHeight = 1.5;
        } else {
            model = this._createGeneralDeviceModel(1.0, stateColor);
            modelHeight = 2;
        }
        model.userData.deviceId = device.id;
        model.userData.deviceData = device;
        model.userData.isDeviceIndicator = true;
        model.userData.isRobot = isRobotType;  // Enable animation for robot types
        group.add(model);

        // ApiDef cubes on top of device (with Flow Zone color)
        if (device.apiDefs && device.apiDefs.length > 0) {
            const apiDefSpacing = 1.0;
            const startX = -(device.apiDefs.length - 1) * apiDefSpacing / 2;
            const apiDefY = modelHeight + 0.5;

            device.apiDefs.forEach((apiDef, apiIdx) => {
                const cube = this._createApiDefCube(apiDef, device.id, flowColor);
                cube.position.set(startX + apiIdx * apiDefSpacing, apiDefY, 0);
                cube.userData.pulseOffset = index * 0.5 + apiIdx * 0.3;
                cube.userData.apiDefIdx = apiIdx;  // 0-based index for direction lookup
                group.add(cube);
            });
        }

        // Device name label (using device-specific label with full name)
        const labelY = modelHeight + (device.apiDefs?.length > 0 ? 2.0 : 1.0);
        const apiDefCount = device.apiDefs?.length || 0;
        const label = this._createDeviceLabel(device.name || device.id, apiDefCount, device.state);
        label.position.set(0, labelY, 0);
        label.userData.deviceId = device.id;
        group.add(label);

        return { group, indicator: model };
    },

    /**
     * Create ApiDef cube with caller count badge
     * @param {object} apiDef - ApiDef data
     * @param {string} deviceId - Parent device ID
     * @param {number|null} flowColor - Optional Flow Zone color (hex) for blending
     */
    _createApiDefCube: function(apiDef, deviceId, flowColor = null) {
        const stateColor = this.stateColors[apiDef.state] || this.stateColors.R;
        const cubeSize = 0.5;

        // Blend state color with flow color if available
        let finalColor = stateColor.hex;
        if (flowColor !== null) {
            // Mix state color (60%) with flow color (40%) for visual distinction
            const stateR = (stateColor.hex >> 16) & 0xff;
            const stateG = (stateColor.hex >> 8) & 0xff;
            const stateB = stateColor.hex & 0xff;

            const flowR = (flowColor >> 16) & 0xff;
            const flowG = (flowColor >> 8) & 0xff;
            const flowB = flowColor & 0xff;

            const mixR = Math.round(stateR * 0.6 + flowR * 0.4);
            const mixG = Math.round(stateG * 0.6 + flowG * 0.4);
            const mixB = Math.round(stateB * 0.6 + flowB * 0.4);

            finalColor = (mixR << 16) | (mixG << 8) | mixB;
        }

        const cubeGeo = new THREE.BoxGeometry(cubeSize, cubeSize, cubeSize);
        const cubeMat = new THREE.MeshStandardMaterial({
            color: finalColor,
            emissive: finalColor,
            emissiveIntensity: 0.4,
            metalness: 0.5,
            roughness: 0.3
        });
        const cube = new THREE.Mesh(cubeGeo, cubeMat);

        cube.userData.apiDefId = apiDef.id;
        cube.userData.apiDefData = apiDef;
        cube.userData.deviceId = deviceId;
        cube.userData.isApiDefCube = true;
        cube.userData.flowColor = flowColor; // Store for updates

        // Caller count badge (if > 1)
        if (apiDef.callerCount > 1) {
            const badge = this._createCallerCountBadge(apiDef.callerCount);
            badge.position.set(cubeSize / 2 + 0.1, cubeSize / 2 + 0.1, 0);
            cube.add(badge);
        }

        cube.castShadow = true;
        return cube;
    },

    /**
     * Create caller count badge sprite
     */
    _createCallerCountBadge: function(count) {
        const canvas = document.createElement('canvas');
        canvas.width = 64;
        canvas.height = 64;
        const ctx = canvas.getContext('2d');

        // Red circle background
        ctx.fillStyle = '#ef4444';
        ctx.beginPath();
        ctx.arc(32, 32, 26, 0, Math.PI * 2);
        ctx.fill();

        // White border
        ctx.strokeStyle = '#ffffff';
        ctx.lineWidth = 2;
        ctx.stroke();

        // Count number
        ctx.fillStyle = '#ffffff';
        ctx.font = 'bold 28px Arial';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(count.toString(), 32, 32);

        const texture = new THREE.CanvasTexture(canvas);
        const spriteMat = new THREE.SpriteMaterial({ map: texture, transparent: true });
        const sprite = new THREE.Sprite(spriteMat);
        sprite.scale.set(0.4, 0.4, 1);

        return sprite;
    },

    /**
     * Create general device model (box)
     */
    _createGeneralDeviceModel: function(scale, stateColor) {
        const group = new THREE.Group();

        // Base platform
        const baseGeo = new THREE.BoxGeometry(2 * scale, 0.3 * scale, 2 * scale);
        const baseMat = new THREE.MeshStandardMaterial({
            color: 0x334155,
            metalness: 0.3,
            roughness: 0.7
        });
        const base = new THREE.Mesh(baseGeo, baseMat);
        base.position.y = 0.15 * scale;
        base.castShadow = true;
        base.receiveShadow = true;
        group.add(base);

        // Main body
        const bodyGeo = new THREE.BoxGeometry(1.5 * scale, 1.5 * scale, 1.5 * scale);
        const bodyMat = new THREE.MeshStandardMaterial({
            color: stateColor.hex,
            metalness: 0.4,
            roughness: 0.6,
            emissive: stateColor.hex,
            emissiveIntensity: 0.2
        });
        const body = new THREE.Mesh(bodyGeo, bodyMat);
        body.position.y = 1.05 * scale;
        body.castShadow = true;
        body.receiveShadow = true;
        group.add(body);

        return group;
    },

    /**
     * Create small device model (cylinder)
     */
    _createSmallDeviceModel: function(scale, stateColor) {
        const group = new THREE.Group();

        // Base
        const baseGeo = new THREE.CylinderGeometry(0.8 * scale, 0.8 * scale, 0.2 * scale, 16);
        const baseMat = new THREE.MeshStandardMaterial({
            color: 0x475569,
            metalness: 0.3,
            roughness: 0.7
        });
        const base = new THREE.Mesh(baseGeo, baseMat);
        base.position.y = 0.1 * scale;
        base.castShadow = true;
        group.add(base);

        // Cylinder body
        const bodyGeo = new THREE.CylinderGeometry(0.5 * scale, 0.6 * scale, 1.2 * scale, 16);
        const bodyMat = new THREE.MeshStandardMaterial({
            color: stateColor.hex,
            metalness: 0.5,
            roughness: 0.4,
            emissive: stateColor.hex,
            emissiveIntensity: 0.3
        });
        const body = new THREE.Mesh(bodyGeo, bodyMat);
        body.position.y = 0.8 * scale;
        body.castShadow = true;
        group.add(body);

        return group;
    },

    /**
     * Create device connections (lines between devices)
     */
    _createDeviceConnections: function(scene, connections, deviceMeshes) {
        if (!connections || connections.length === 0) return;

        connections.forEach(conn => {
            const fromMesh = deviceMeshes[conn.from];
            const toMesh = deviceMeshes[conn.to];

            if (!fromMesh || !toMesh) return;

            // Get world positions
            const fromPos = new THREE.Vector3();
            const toPos = new THREE.Vector3();
            fromMesh.getWorldPosition(fromPos);
            toMesh.getWorldPosition(toPos);

            // Create curved line
            const midPoint = new THREE.Vector3(
                (fromPos.x + toPos.x) / 2,
                Math.max(fromPos.y, toPos.y) + 2,
                (fromPos.z + toPos.z) / 2
            );

            const curve = new THREE.QuadraticBezierCurve3(fromPos, midPoint, toPos);
            const points = curve.getPoints(30);
            const lineGeo = new THREE.BufferGeometry().setFromPoints(points);

            const lineMat = new THREE.LineBasicMaterial({
                color: 0xfbbf24,
                opacity: 0.6,
                transparent: true
            });

            const line = new THREE.Line(lineGeo, lineMat);
            scene.add(line);
        });
    },

    /**
     * Fit camera to view all devices
     */
    _fitCameraToDevices: function(scene, camera, controls, devices) {
        if (!devices || devices.length === 0) return;

        let minX = Infinity, maxX = -Infinity;
        let minZ = Infinity, maxZ = -Infinity;

        devices.forEach(d => {
            minX = Math.min(minX, d.posX || 0);
            maxX = Math.max(maxX, d.posX || 0);
            minZ = Math.min(minZ, d.posZ || 0);
            maxZ = Math.max(maxZ, d.posZ || 0);
        });

        const centerX = (minX + maxX) / 2;
        const centerZ = (minZ + maxZ) / 2;
        const width = maxX - minX;
        const depth = maxZ - minZ;
        const maxDim = Math.max(width, depth, 20);

        camera.position.set(centerX, maxDim * 0.8, centerZ + maxDim * 0.9);
        controls.target.set(centerX, 0, centerZ);
        controls.update();
    },

    /**
     * Create raycaster from mouse event
     */
    _createRaycaster: function(elementId, event) {
        const sceneData = this._scenes[elementId];
        const canvas = sceneData.renderer.domElement;
        const rect = canvas.getBoundingClientRect();

        const mouse = new THREE.Vector2(
            ((event.clientX - rect.left) / rect.width) * 2 - 1,
            -((event.clientY - rect.top) / rect.height) * 2 + 1
        );

        const raycaster = new THREE.Raycaster();
        raycaster.setFromCamera(mouse, sceneData.camera);
        return raycaster;
    },

    /**
     * Get device intersects from mouse event
     */
    _getDeviceIntersects: function(elementId, event) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return [];

        const raycaster = this._createRaycaster(elementId, event);

        // Get all device groups
        const deviceObjects = [];
        Object.values(sceneData.deviceMeshes).forEach(group => {
            if (group) {
                group.traverse(obj => {
                    if (obj.isMesh && !obj.userData?.isFloor && !obj.userData?.isFlowZone) {
                        deviceObjects.push(obj);
                    }
                });
            }
        });

        return raycaster.intersectObjects(deviceObjects, false);
    },

    /**
     * Recalculate flow zones based on device positions
     */
    _recalculateFlowZonesForDevices: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.devices) return;

        console.log('Recalculating flow zones...');

        // Group devices by flow name
        const flowGroups = {};
        sceneData.devices.forEach(device => {
            const flowName = device.flowName || 'Default';
            if (!flowGroups[flowName]) {
                flowGroups[flowName] = [];
            }
            flowGroups[flowName].push(device);
        });

        // Recalculate each flow zone
        Object.entries(flowGroups).forEach(([flowName, devices]) => {
            if (devices.length === 0) return;

            // Calculate bounding box
            let minX = Infinity, maxX = -Infinity;
            let minZ = Infinity, maxZ = -Infinity;

            devices.forEach(d => {
                const x = d.posX || 0;
                const z = d.posZ || 0;
                minX = Math.min(minX, x);
                maxX = Math.max(maxX, x);
                minZ = Math.min(minZ, z);
                maxZ = Math.max(maxZ, z);
            });

            const padding = 4;
            const centerX = (minX + maxX) / 2;
            const centerZ = (minZ + maxZ) / 2;
            const sizeX = Math.max(maxX - minX + padding * 2, 8);
            const sizeZ = Math.max(maxZ - minZ + padding * 2, 8);

            // Update flow zone mesh
            const zoneName = `flowZone_${flowName}`;
            const zoneMesh = sceneData.scene.getObjectByName(zoneName);

            if (zoneMesh) {
                // Update geometry
                if (zoneMesh.geometry) {
                    zoneMesh.geometry.dispose();
                }
                zoneMesh.geometry = new THREE.PlaneGeometry(sizeX, sizeZ);
                zoneMesh.position.set(centerX, 0.02, centerZ);

                // Update border
                const borderName = `${zoneName}_border`;
                const border = sceneData.scene.getObjectByName(borderName);
                if (border) {
                    if (border.geometry) {
                        border.geometry.dispose();
                    }

                    const borderShape = new THREE.Shape();
                    borderShape.moveTo(-sizeX / 2, -sizeZ / 2);
                    borderShape.lineTo(sizeX / 2, -sizeZ / 2);
                    borderShape.lineTo(sizeX / 2, sizeZ / 2);
                    borderShape.lineTo(-sizeX / 2, sizeZ / 2);
                    borderShape.lineTo(-sizeX / 2, -sizeZ / 2);

                    border.geometry = new THREE.BufferGeometry().setFromPoints(borderShape.getPoints());
                    border.position.set(centerX, 0.03, centerZ);
                }

                // Update label position
                const labelName = `${zoneName}_label`;
                const label = sceneData.scene.getObjectByName(labelName);
                if (label) {
                    const labelX = centerX - sizeX / 2 + 2;
                    const labelZ = centerZ - sizeZ / 2 - 1;  // Outside zone boundary
                    label.position.set(labelX, 1.5, labelZ);
                }

                console.log(`✓ Updated flow zone '${flowName}' at (${centerX.toFixed(1)}, ${centerZ.toFixed(1)}) size (${sizeX.toFixed(1)}, ${sizeZ.toFixed(1)})`);
            }
        });
    },

    /**
     * Handle click on device or ApiDef cube
     */
    _handleDeviceClick: function(elementId, event) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const intersects = this._getRaycasterIntersects(elementId, event);

        for (const hit of intersects) {
            const obj = hit.object;

            // ApiDef cube clicked
            if (obj.userData?.isApiDefCube) {
                const apiDefId = obj.userData.apiDefId;
                const apiDefData = obj.userData.apiDefData;
                const deviceId = obj.userData.deviceId;
                const screenPos = this._getScreenPosition(elementId, obj);

                console.log('=== ApiDef cube clicked ===');
                console.log('ApiDef:', apiDefData.name, 'Callers:', apiDefData.callerCount);

                if (sceneData.callSelectionCallback) {
                    sceneData.callSelectionCallback.invokeMethodAsync(
                        'OnApiDefSelected', apiDefId, deviceId, apiDefData, screenPos.x, screenPos.y
                    );
                }
                return;
            }

            // Device clicked
            if (obj.userData?.isDeviceIndicator || obj.userData?.deviceId) {
                const deviceId = obj.userData.deviceId;
                const screenPos = this._getScreenPosition(elementId, obj);

                console.log('=== Device clicked ===');
                console.log('Device:', deviceId);

                // Build deviceData object from userData or use defaults
                const deviceData = obj.userData.deviceData || {
                    name: deviceId,
                    deviceType: obj.userData.deviceType || 'general',
                    state: obj.userData.state || 'R'
                };

                if (sceneData.callSelectionCallback) {
                    sceneData.callSelectionCallback.invokeMethodAsync(
                        'OnDeviceSelected', deviceId, deviceData, screenPos.x, screenPos.y
                    );
                }
                return;
            }
        }

        // Empty space clicked
        if (sceneData.callSelectionCallback) {
            sceneData.callSelectionCallback.invokeMethodAsync('OnEmptySpaceClicked');
        }
    },

    /**
     * Handle mouse hover for device highlighting
     */
    _handleDeviceHover: function(elementId, event) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const intersects = this._getRaycasterIntersects(elementId, event);

        // Reset previously hovered
        if (sceneData.hoveredObject) {
            if (sceneData.hoveredObject.material) {
                sceneData.hoveredObject.material.emissiveIntensity =
                    sceneData.hoveredObject.userData.originalEmissive || 0.2;
            }
            sceneData.hoveredObject = null;
        }

        for (const hit of intersects) {
            const obj = hit.object;
            if (obj.userData?.isApiDefCube || obj.userData?.isDeviceIndicator) {
                if (obj.material) {
                    obj.userData.originalEmissive = obj.material.emissiveIntensity;
                    obj.material.emissiveIntensity = 0.8;
                }
                sceneData.hoveredObject = obj;
                break;
            }
        }
    },

    /**
     * Update device states
     */
    updateDeviceStates: function(elementId, deviceStates) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        deviceStates.forEach(({ id, state }) => {
            const deviceGroup = sceneData.deviceMeshes[id];
            if (!deviceGroup) return;
            const color = this.stateColors[state] || this.stateColors.R;

            // Update deviceData.state for robot animation loop
            const device = sceneData.devices.find(d => d.id === id);
            if (device) device.state = state;
            if (deviceGroup.userData?.deviceData) deviceGroup.userData.deviceData.state = state;

            // Traverse entire model tree (handles robots which are Groups)
            deviceGroup.traverse(child => {
                // Skip ApiDef cubes - they have their own state management
                if (child.userData?.isApiDefCube) return;

                if (child.userData?.deviceData) child.userData.deviceData.state = state;
                if (child.isMesh && child.material && child.material.emissive !== undefined) {
                    child.material.color.setHex(color.hex);
                    child.material.emissive.setHex(color.hex);
                    child.material.emissiveIntensity = state === 'G' ? 0.5 : 0.3;
                }
            });

            // ── Lib3D 모션 애니메이션 트리거 ─────────────────────────────
            // Device state 기반으로 Lib3D 모델 애니메이션 직접 제어
            const libModel = deviceGroup.children.find(c => c.userData && c.userData.isDeviceIndicator);
            if (libModel && window.Ds2View3DLibrary) {
                // Lib3D 상태 표시 업데이트 (상태 인디케이터 색상)
                window.Ds2View3DLibrary.updateState(libModel, state);

                const modelType = libModel.userData.deviceType;
                const dirs = window.Ds2View3DLibrary.deviceTypes[modelType]?.dirs;
                if (dirs && dirs.length > 0) {
                    if (state === 'G') {
                        // ApiDef별 방향이 이미 설정되지 않은 경우에만 기본(첫번째) 방향 사용
                        if (!libModel.userData.activeAnimation) {
                            libModel.userData.activeAnimation = dirs[0];
                        }
                    } else {
                        libModel.userData.activeAnimation = null;
                    }
                }

                // 설비 사운드 (Going → 재생, 기타 → 정지)
                if (window.Ds2Sound) {
                    if (state === 'G') window.Ds2Sound.play(id, libModel.userData.deviceType);
                    else              window.Ds2Sound.stop(id);
                }
            }
        });

    },

    /**
     * Reset all device states to Ready (green)
     */
    resetDeviceStates: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const readyColor = this.stateColors.R;
        let count = 0;

        // Reset all devices to Ready state
        sceneData.devices.forEach(device => {
            device.state = 'R';
            const deviceGroup = sceneData.deviceMeshes[device.id];
            if (!deviceGroup) return;

            if (deviceGroup.userData?.deviceData) {
                deviceGroup.userData.deviceData.state = 'R';
            }

            // Traverse entire model tree
            deviceGroup.traverse(child => {
                // Skip ApiDef cubes - they have their own state management
                if (child.userData?.isApiDefCube) return;

                if (child.userData?.deviceData) {
                    child.userData.deviceData.state = 'R';
                }
                if (child.isMesh && child.material && child.material.emissive !== undefined) {
                    child.material.color.setHex(readyColor.hex);
                    child.material.emissive.setHex(readyColor.hex);
                    child.material.emissiveIntensity = 0.3;
                }
            });

            // 애니메이션 초기화
            const libModel = deviceGroup.children.find(c => c.userData && c.userData.isDeviceIndicator);
            if (libModel) libModel.userData.activeAnimation = null;

            count++;
        });

        // 전체 설비 사운드 정지
        if (window.Ds2Sound) window.Ds2Sound.stopAll();
    },

    /**
     * Update ApiDef cube states (individual ApiDef color)
     * Device state is yellow if ANY ApiDef is Going, otherwise green
     */
    updateApiDefStates: function(elementId, apiDefStates) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        apiDefStates.forEach(({ id, state }) => {
            const stateColor = this.stateColors[state] || this.stateColors.R;
            let found = false;

            // Find all ApiDef cubes with this ID across all device stations
            Object.values(sceneData.deviceMeshes).forEach(deviceGroup => {
                deviceGroup.traverse(child => {
                    if (child.userData?.isApiDefCube && child.userData.apiDefId === id) {
                        found = true;
                        // Update ApiDef data
                        if (child.userData.apiDefData) {
                            child.userData.apiDefData.state = state;
                        }

                        // Blend state color with flow color (60% state + 40% flow)
                        let finalColor = stateColor.hex;
                        const flowColor = child.userData.flowColor;

                        if (flowColor !== null && flowColor !== undefined) {
                            const stateR = (stateColor.hex >> 16) & 0xff;
                            const stateG = (stateColor.hex >> 8) & 0xff;
                            const stateB = stateColor.hex & 0xff;

                            const flowR = (flowColor >> 16) & 0xff;
                            const flowG = (flowColor >> 8) & 0xff;
                            const flowB = flowColor & 0xff;

                            const mixR = Math.round(stateR * 0.6 + flowR * 0.4);
                            const mixG = Math.round(stateG * 0.6 + flowG * 0.4);
                            const mixB = Math.round(stateB * 0.6 + flowB * 0.4);

                            finalColor = (mixR << 16) | (mixG << 8) | mixB;
                        }

                        // Update material colors
                        if (child.isMesh && child.material) {
                            child.material.color.setHex(finalColor);
                            child.material.emissive.setHex(finalColor);
                            child.material.emissiveIntensity = state === 'G' ? 0.5 : 0.4;
                        }

                        // Motion animation: use apiDefIdx to resolve animation direction (index-based, name-independent)
                        const model = deviceGroup.children.find(c => c.userData.isDeviceIndicator);
                        if (model) {
                            const apiDefIdx = child.userData.apiDefIdx ?? 0;
                            const modelType = model.userData.deviceType;
                            const lib = window.Ds2View3DLibrary;
                            const dirs = lib?.deviceTypes[modelType]?.dirs;
                            const animDir = dirs?.[apiDefIdx];
                            if (animDir) {
                                if (state === 'G') {
                                    model.userData.activeAnimation = animDir;
                                } else if (model.userData.activeAnimation === animDir) {
                                    model.userData.activeAnimation = null;
                                }
                            }
                        }
                    }
                });
            });
        });
    },

    /**
     * Show ApiDef connection arrows (3D arrows between ApiDef cubes)
     * @param {string} elementId - Scene element ID
     * @param {string} deviceId - Device GUID
     * @param {string} apiDefName - ApiDef name
     * @param {Array} outgoing - Array of {deviceId, apiDefName} for outgoing connections
     * @param {Array} incoming - Array of {deviceId, apiDefName} for incoming connections
     */
    showApiDefConnections: function(elementId, deviceId, apiDefName, outgoing, incoming) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        this._clearApiDefConnections(elementId);

        // Find source ApiDef cube position
        const sourceCube = this._findApiDefCube(sceneData, deviceId, apiDefName);
        if (!sourceCube) {
            console.warn(`Source ApiDef cube ${apiDefName} not found on device ${deviceId}`);
            return;
        }

        const sourcePos = new THREE.Vector3();
        sourceCube.getWorldPosition(sourcePos);

        const OUTGOING_COLOR = 0x10b981;  // Green for outgoing
        const INCOMING_COLOR = 0xf59e0b;  // Amber for incoming

        const arrows = [
            ...this._createApiDefCubeArrows(sceneData, sourcePos, sourceCube, outgoing, OUTGOING_COLOR, false),
            ...this._createApiDefCubeArrows(sceneData, sourcePos, sourceCube, incoming, INCOMING_COLOR, true)
        ];

        arrows.forEach(arrow => sceneData.scene.add(arrow));
        sceneData.apiDefArrows = arrows;

        // Highlight source cube
        this._highlightApiDefCube(sourceCube, 0x3b82f6, 1.2);

        console.log(`✓ Created ${arrows.length} ApiDef connection arrows for ${apiDefName} (${outgoing.length} outgoing, ${incoming.length} incoming)`);
    },

    /**
     * Find ApiDef cube in the scene
     * @param {object} sceneData - Scene data
     * @param {string} deviceId - Device GUID
     * @param {string} apiDefName - ApiDef name
     * @returns {THREE.Mesh|null} ApiDef cube or null
     */
    _findApiDefCube: function(sceneData, deviceId, apiDefName) {
        const deviceGroup = sceneData.deviceMeshes[deviceId];
        if (!deviceGroup) return null;

        let foundCube = null;
        deviceGroup.traverse(child => {
            if (child.userData?.isApiDefCube &&
                child.userData.apiDefData?.name === apiDefName) {
                foundCube = child;
            }
        });

        return foundCube;
    },

    /**
     * Create arrows from source ApiDef cube to target ApiDef cubes
     * @param {object} sceneData - Scene data
     * @param {THREE.Vector3} sourcePos - Source cube world position
     * @param {THREE.Mesh} sourceCube - Source cube mesh
     * @param {Array} connections - Array of {deviceId, apiDefName}
     * @param {number} color - Arrow color
     * @param {boolean} reversed - If true, arrows point TO source (incoming)
     * @returns {Array} Array of arrow groups
     */
    _createApiDefCubeArrows: function(sceneData, sourcePos, sourceCube, connections, color, reversed) {
        return connections.map(conn => {
            const targetCube = this._findApiDefCube(sceneData, conn.deviceId, conn.apiDefName);
            if (!targetCube) {
                console.warn(`Target ApiDef cube ${conn.apiDefName} not found on device ${conn.deviceId}`);
                return null;
            }

            const targetPos = new THREE.Vector3();
            targetCube.getWorldPosition(targetPos);

            // Highlight target cube
            this._highlightApiDefCube(targetCube, color, 1.1);

            // Create arrow (reversed means arrow points TO source)
            const [start, end] = reversed
                ? [targetPos, sourcePos]
                : [sourcePos, targetPos];

            return this._create3DApiDefArrow(start, end, color, reversed);
        }).filter(arrow => arrow !== null);
    },

    /**
     * Highlight an ApiDef cube with color and scale
     * @param {THREE.Mesh} cube - ApiDef cube mesh
     * @param {number} color - Highlight color
     * @param {number} scale - Scale multiplier
     */
    _highlightApiDefCube: function(cube, color, scale) {
        if (!cube || !cube.material) return;

        cube.userData.originalColor = cube.material.color.getHex();
        cube.userData.originalScale = cube.scale.x;

        cube.material.emissive.setHex(color);
        cube.material.emissiveIntensity = 0.8;
        cube.scale.setScalar(scale);
    },

    /**
     * Create arrows for a set of connections (legacy device-to-device arrows)
     */
    _createDirectedArrows: function(sceneData, sourceDevice, connections, color, reversed) {
        return connections.map(conn => {
            const targetDevice = sceneData.devices.find(d => d.id === conn.deviceId);
            if (!targetDevice) return null;

            const [x1, z1, x2, z2] = reversed
                ? [targetDevice.posX, targetDevice.posZ, sourceDevice.posX, sourceDevice.posZ]
                : [sourceDevice.posX, sourceDevice.posZ, targetDevice.posX, targetDevice.posZ];

            return this._createConnectionArrow(x1, 0, z1, x2, 0, z2, color, true);
        }).filter(arrow => arrow !== null);
    },

    /**
     * Clear ApiDef connection arrows and reset highlighted cubes
     */
    _clearApiDefConnections: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Remove arrows
        if (sceneData.apiDefArrows) {
            sceneData.apiDefArrows.forEach(arrow => {
                sceneData.scene.remove(arrow);
                // Dispose geometries and materials
                arrow.traverse(child => {
                    if (child.geometry) child.geometry.dispose();
                    if (child.material) {
                        if (Array.isArray(child.material)) {
                            child.material.forEach(m => m.dispose());
                        } else {
                            child.material.dispose();
                        }
                    }
                });
            });
            sceneData.apiDefArrows = [];
        }

        // Reset highlighted cubes
        if (sceneData.deviceMeshes) {
            Object.values(sceneData.deviceMeshes).forEach(deviceGroup => {
                deviceGroup.traverse(child => {
                    if (child.userData?.isApiDefCube &&
                        child.userData.originalColor !== undefined) {
                        // Restore original color and scale
                        if (child.material) {
                            child.material.emissive.setHex(child.userData.originalColor);
                            child.material.emissiveIntensity = 0.4;
                        }
                        if (child.userData.originalScale !== undefined) {
                            child.scale.setScalar(child.userData.originalScale);
                        }
                        // Clear stored values
                        delete child.userData.originalColor;
                        delete child.userData.originalScale;
                    }
                });
            });
        }
    },

    /**
     * Create a connection arrow between two points
     */
    _createConnectionArrow: function(x1, y1, z1, x2, y2, z2, color, dashed) {
        const ARROW_HEIGHT = 2;
        const DASH_SIZE = 0.5;
        const GAP_SIZE = 0.3;
        const LINE_WIDTH = 2;
        const CONE_RADIUS = 0.3;
        const CONE_HEIGHT = 1;
        const CONE_SEGMENTS = 8;

        const dir = new THREE.Vector3(x2 - x1, y2 - y1, z2 - z1);
        dir.normalize();

        const group = new THREE.Group();

        // Line
        const material = dashed
            ? new THREE.LineDashedMaterial({ color, dashSize: DASH_SIZE, gapSize: GAP_SIZE, linewidth: LINE_WIDTH })
            : new THREE.LineBasicMaterial({ color, linewidth: LINE_WIDTH });

        const geometry = new THREE.BufferGeometry().setFromPoints([
            new THREE.Vector3(x1, y1 + ARROW_HEIGHT, z1),
            new THREE.Vector3(x2, y2 + ARROW_HEIGHT, z2)
        ]);
        const line = new THREE.Line(geometry, material);
        if (dashed) line.computeLineDistances();
        group.add(line);

        // Arrow head (cone)
        const cone = new THREE.Mesh(
            new THREE.ConeGeometry(CONE_RADIUS, CONE_HEIGHT, CONE_SEGMENTS),
            new THREE.MeshBasicMaterial({ color })
        );
        cone.position.set(x2, y2 + ARROW_HEIGHT, z2);
        cone.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir);
        group.add(cone);

        return group;
    },

    /**
     * Create a 3D arrow between two ApiDef cubes (Bezier curve with tube and arrow head)
     * @param {THREE.Vector3} start - Start position
     * @param {THREE.Vector3} end - End position
     * @param {number} color - Arrow color
     * @param {boolean} reversed - If true, this is an incoming arrow
     * @returns {THREE.Group} Arrow group
     */
    _create3DApiDefArrow: function(start, end, color, reversed) {
        const group = new THREE.Group();
        group.userData.isApiDefArrow = true;

        // Calculate control point for Bezier curve (arc upward)
        const mid = new THREE.Vector3().lerpVectors(start, end, 0.5);
        const distance = start.distanceTo(end);
        const arcHeight = Math.min(distance * 0.3, 5.0);  // Arc height based on distance
        mid.y += arcHeight;

        // Create Bezier curve
        const curve = new THREE.QuadraticBezierCurve3(start, mid, end);
        const points = curve.getPoints(50);

        // Create tube along the curve
        const tubeGeometry = new THREE.TubeGeometry(
            new THREE.CatmullRomCurve3(points),
            50,          // segments
            0.15,        // radius
            8,           // radial segments
            false        // closed
        );

        const tubeMaterial = new THREE.MeshStandardMaterial({
            color: color,
            emissive: color,
            emissiveIntensity: 0.5,
            metalness: 0.3,
            roughness: 0.4
        });

        const tube = new THREE.Mesh(tubeGeometry, tubeMaterial);
        tube.userData.isArrowTube = true;
        group.add(tube);

        // Arrow head (cone) at the end
        const direction = new THREE.Vector3().subVectors(points[points.length - 1], points[points.length - 5]).normalize();
        const arrowHead = new THREE.Mesh(
            new THREE.ConeGeometry(0.4, 1.2, 12),
            new THREE.MeshStandardMaterial({
                color: color,
                emissive: color,
                emissiveIntensity: 0.6,
                metalness: 0.5,
                roughness: 0.3
            })
        );

        arrowHead.position.copy(end);
        arrowHead.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), direction);
        arrowHead.userData.isArrowHead = true;
        group.add(arrowHead);

        // Add animated flow effect (optional particles or glow)
        this._addArrowFlowEffect(group, points, color, reversed);

        return group;
    },

    /**
     * Add animated flow effect to arrow (moving particles)
     * @param {THREE.Group} arrowGroup - Arrow group
     * @param {Array} curvePoints - Points along the curve
     * @param {number} color - Arrow color
     * @param {boolean} reversed - Flow direction
     */
    _addArrowFlowEffect: function(arrowGroup, curvePoints, color, reversed) {
        // Create small spheres that will animate along the curve
        const particleCount = 3;
        const particles = [];

        for (let i = 0; i < particleCount; i++) {
            const particle = new THREE.Mesh(
                new THREE.SphereGeometry(0.2, 8, 8),
                new THREE.MeshBasicMaterial({
                    color: color,
                    transparent: true,
                    opacity: 0.8
                })
            );

            particle.userData.curvePoints = curvePoints;
            particle.userData.progress = i / particleCount;
            particle.userData.reversed = false;  // Always flow start→end (geometry already handles direction)
            particle.userData.isFlowParticle = true;

            arrowGroup.add(particle);
            particles.push(particle);
        }

        arrowGroup.userData.flowParticles = particles;
    },

    /**
     * Fit camera to show all placed devices
     */
    fitCameraToDevices: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.devices || sceneData.devices.length === 0) return;
        const points = sceneData.devices.map(d => ({ posX: d.posX, posZ: d.posZ }));
        this._fitCameraToScene(sceneData.scene, sceneData.camera, sceneData.controls, points);
        console.log(`Camera fitted to ${sceneData.devices.length} devices`);
    },

    /**
     * Re-calculate and reposition all devices with auto-layout (grouped by flow)
     */
    autoLayoutDevices: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.devices || sceneData.devices.length === 0) return;

        const devices = sceneData.devices;
        const flowGroups = {};
        devices.forEach(d => {
            const fn = d.flowName || 'Default';
            if (!flowGroups[fn]) flowGroups[fn] = [];
            flowGroups[fn].push(d);
        });

        const flowNames = Object.keys(flowGroups);
        const flowCount = flowNames.length;
        const spacing = 12;
        const flowSpacing = Math.max(20, spacing * Math.ceil(Math.sqrt(
            Math.max(...flowNames.map(fn => flowGroups[fn].length))
        )));

        flowNames.forEach((flowName, flowIdx) => {
            const flowDevices = flowGroups[flowName];
            const cols = Math.ceil(Math.sqrt(flowDevices.length));
            const totalRows = Math.ceil(flowDevices.length / cols);
            const flowOffsetZ = (flowIdx - (flowCount - 1) / 2) * flowSpacing;

            flowDevices.forEach((device, idx) => {
                const col = idx % cols;
                const row = Math.floor(idx / cols);
                const x = (col - (cols - 1) / 2) * spacing;
                const z = (row - (totalRows - 1) / 2) * spacing + flowOffsetZ;

                // Move the existing scene group
                sceneData.scene.traverse(obj => {
                    if (obj.isGroup && obj.userData.deviceId === device.id) {
                        obj.position.set(x, 0, z);
                    }
                });
                device.posX = x;
                device.posZ = z;
            });
        });

        // Persist new positions
        const positions = {};
        devices.forEach(d => { positions[d.id] = { x: d.posX, z: d.posZ }; });
        this._saveDevicePositions(elementId, positions);

        this.fitCameraToDevices(elementId);
        console.log(`Auto-layout applied to ${devices.length} devices`);
    },

    /**
     * Clear saved positions and run auto-layout
     */
    resetDeviceLayout: function(elementId) {
        try {
            const key = `ev2_device_positions_${elementId}`;
            localStorage.removeItem(key);
        } catch (e) { /* ignore */ }
        this.autoLayoutDevices(elementId);
        console.log(`Device layout reset for ${elementId}`);
    },

    /**
     * Load device positions from localStorage
     */
    _loadDevicePositions: function(elementId) {
        try {
            const key = `ev2_device_positions_${elementId}`;
            const saved = localStorage.getItem(key);
            return saved ? JSON.parse(saved) : null;
        } catch (e) {
            console.warn('Failed to load device positions:', e);
            return null;
        }
    },

    /**
     * Save device positions to localStorage
     */
    _saveDevicePositions: function(elementId, positions) {
        try {
            const key = `ev2_device_positions_${elementId}`;
            localStorage.setItem(key, JSON.stringify(positions));
        } catch (e) {
            console.warn('Failed to save device positions:', e);
        }
    },

    /**
     * Get stored device positions from localStorage (public - callable from Blazor)
     * @param {string} elementId - Scene identifier
     * @returns {object} Object with deviceId as key and {x, z} as value, or empty object
     */
    getStoredDevicePositions: function(elementId) {
        const positions = this._loadDevicePositions(elementId);
        console.log(`getStoredDevicePositions(${elementId}):`, positions ? Object.keys(positions).length : 0, 'devices');
        return positions || {};
    },

    // ============================================================
    // Drag-and-Drop and Auto-Layout Functions
    // ============================================================

    /**
     * Convert screen coordinates to 3D world position (on ground plane)
     * @param {string} elementId - Scene identifier
     * @param {number} clientX - Screen X coordinate
     * @param {number} clientY - Screen Y coordinate
     * @returns {number[]} [worldX, worldZ] coordinates
     */
    screenToWorldPosition: function(elementId, clientX, clientY) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn(`Scene '${elementId}' not found`);
            return [0, 0];
        }

        const container = document.getElementById(elementId);
        if (!container) return [0, 0];

        const rect = container.getBoundingClientRect();

        // Normalized device coordinates (-1 to +1)
        const mouse = new THREE.Vector2(
            ((clientX - rect.left) / rect.width) * 2 - 1,
            -((clientY - rect.top) / rect.height) * 2 + 1
        );

        // Raycast to ground plane (Y = 0)
        const raycaster = new THREE.Raycaster();
        raycaster.setFromCamera(mouse, sceneData.camera);

        const groundPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
        const intersection = new THREE.Vector3();

        if (raycaster.ray.intersectPlane(groundPlane, intersection)) {
            console.log(`Screen (${clientX}, ${clientY}) -> World (${intersection.x.toFixed(2)}, ${intersection.z.toFixed(2)})`);
            return [intersection.x, intersection.z];
        }

        return [0, 0];
    },

    /**
     * Add a device at specified 3D position (with ApiDef cubes)
     * @param {string} elementId - Scene identifier
     * @param {object} deviceData - Device data { deviceId, deviceName, deviceType, flowName, state, apiDefs }
     * @param {number} x - World X position
     * @param {number} z - World Z position
     */
    addDeviceAtPosition: function(elementId, deviceData, x, z) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn(`Scene '${elementId}' not found`);
            return;
        }

        // Defensive null checks for deviceData
        if (!deviceData) {
            console.error('addDeviceAtPosition: deviceData is null or undefined');
            return;
        }

        const deviceName = deviceData.deviceName || deviceData.name || 'Unknown';
        const deviceId = deviceData.deviceId || deviceData.id || `device_${Date.now()}`;
        const apiDefs = Array.isArray(deviceData.apiDefs) ? deviceData.apiDefs : [];

        console.log(`Adding device '${deviceName}' at (${x.toFixed(2)}, ${z.toFixed(2)}) with ${apiDefs.length} ApiDefs`);

        // Create device object with ApiDefs (robust null handling)
        const device = {
            id: deviceId,
            name: deviceName,
            modelType: deviceData.modelType || '',
            deviceType: deviceData.deviceType || 'general',
            flowName: deviceData.flowName || 'Default',
            state: deviceData.state || 'R',
            posX: x,
            posY: 0,
            posZ: z,
            apiDefs: apiDefs.map(a => ({
                id: a?.id || '',
                name: a?.name || '',
                guid: a?.guid || '',
                callerCount: a?.callerCount || 0,
                state: a?.state || 'R'
            }))
        };

        // Create device station
        const station = this._createDeviceStation(device, sceneData.devices.length, sceneData.scene);
        if (!station || !station.group) {
            console.error(`Failed to create device station for '${device.name}'`);
            return;
        }

        sceneData.scene.add(station.group);
        sceneData.deviceMeshes[device.id] = station.group;  // Store group (contains ApiDef cubes), not just indicator
        sceneData.devices.push(device);

        // Save position to localStorage
        const positions = this._loadDevicePositions(elementId) || {};
        positions[device.id] = { x, z };
        this._saveDevicePositions(elementId, positions);

        // Update call cubes cache for chain visualization
        this._updateCallCubesCache(elementId);

        // Center camera on all objects
        this._centerCameraOnAllObjects(elementId);

        console.log(`✓ Device '${device.name}' added to scene with ${device.apiDefs.length} ApiDefs`);
    },

    /**
     * Center camera on all objects without changing floor size
     * @param {string} elementId - Scene identifier
     */
    _centerCameraOnAllObjects: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.camera || !sceneData.controls) return;

        const allPositions = [];

        // Collect all device positions
        if (sceneData.devices && sceneData.devices.length > 0) {
            sceneData.devices.forEach(device => {
                allPositions.push({ x: device.posX || 0, z: device.posZ || 0 });
            });
        }

        // Collect all work positions (if any)
        if (sceneData.config && sceneData.config.works) {
            sceneData.config.works.forEach(work => {
                allPositions.push({ x: work.posX || 0, z: work.posZ || 0 });
            });
        }

        if (allPositions.length === 0) {
            // No objects, center at origin
            sceneData.controls.target.set(0, 0, 0);
            sceneData.controls.update();
            return;
        }

        // Calculate center point of all objects
        const xs = allPositions.map(p => p.x);
        const zs = allPositions.map(p => p.z);
        const centerX = (Math.min(...xs) + Math.max(...xs)) / 2;
        const centerZ = (Math.min(...zs) + Math.max(...zs)) / 2;

        // Calculate span for camera distance
        const spanX = Math.max(...xs) - Math.min(...xs);
        const spanZ = Math.max(...zs) - Math.min(...zs);
        const maxSpan = Math.max(spanX, spanZ, 50);

        // Position camera at consistent angle (45 degrees from top-right)
        const cameraHeight = Math.max(maxSpan * 0.7, 40);
        const cameraDistance = Math.max(maxSpan * 1.2, 60);

        // Set camera to look at center from 45-degree angle
        sceneData.camera.position.set(
            centerX + cameraDistance * 0.5,
            cameraHeight,
            centerZ + cameraDistance * 0.8
        );

        sceneData.controls.target.set(centerX, 0, centerZ);
        sceneData.controls.update();

        console.log(`[Camera] Centered on (${centerX.toFixed(1)}, ${centerZ.toFixed(1)}) with ${allPositions.length} objects, span: ${maxSpan.toFixed(1)}`);
    },

    /**
     * Place all devices at once with auto-layout (with ApiDef cubes)
     * @param {string} elementId - Scene identifier
     * @param {Array} devices - Array of device data { deviceId, deviceName, deviceType, flowName, state, apiDefs }
     */
    placeAllDevices: function(elementId, devices) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn(`Scene '${elementId}' not found`);
            return;
        }

        // Prevent duplicate calls (JavaScript level guard)
        if (sceneData._placingAllDevices) {
            console.warn(`placeAllDevices already in progress for '${elementId}', ignoring duplicate call`);
            return;
        }
        sceneData._placingAllDevices = true;

        // Defensive null check
        if (!Array.isArray(devices)) {
            console.error('placeAllDevices: devices is not an array');
            sceneData._placingAllDevices = false;
            return;
        }

        console.log(`Placing ${devices.length} devices with auto-layout`);

        // Group devices by flowName for better layout
        const flowGroups = {};
        devices.forEach(d => {
            if (!d) return; // Skip null/undefined entries
            const flowName = d.flowName || 'Default';
            if (!flowGroups[flowName]) flowGroups[flowName] = [];
            flowGroups[flowName].push(d);
        });

        const flowNames = Object.keys(flowGroups);
        const flowCount = flowNames.length;

        // Calculate layout positions
        let deviceIndex = 0;
        let successCount = 0;
        const spacing = 12; // spacing between devices
        const flowSpacing = 20; // spacing between flows

        flowNames.forEach((flowName, flowIdx) => {
            const flowDevices = flowGroups[flowName];
            const cols = Math.ceil(Math.sqrt(flowDevices.length));
            const flowOffsetZ = (flowIdx - (flowCount - 1) / 2) * flowSpacing;

            flowDevices.forEach((deviceData, idx) => {
                const row = Math.floor(idx / cols);
                const col = idx % cols;
                const totalRows = Math.ceil(flowDevices.length / cols);

                // Calculate position
                const x = (col - (cols - 1) / 2) * spacing;
                const z = (row - (totalRows - 1) / 2) * spacing + flowOffsetZ;

                // Robust null handling for deviceData
                const deviceName = deviceData.deviceName || deviceData.name || 'Unknown';
                const deviceId = deviceData.deviceId || deviceData.id || `device_${deviceIndex}`;
                const apiDefs = Array.isArray(deviceData.apiDefs) ? deviceData.apiDefs : [];

                // Create device object with ApiDefs
                const device = {
                    id: deviceId,
                    name: deviceName,
                    modelType: deviceData.modelType || '',
                    deviceType: deviceData.deviceType || 'general',
                    flowName: flowName,
                    state: deviceData.state || 'R',
                    posX: x,
                    posY: 0,
                    posZ: z,
                    apiDefs: apiDefs.map(a => ({
                        id: a?.id || '',
                        name: a?.name || '',
                        guid: a?.guid || '',
                        callerCount: a?.callerCount || 0,
                        state: a?.state || 'R'
                    }))
                };

                // Create device station
                const station = this._createDeviceStation(device, sceneData.devices.length, sceneData.scene);
                if (!station || !station.group) {
                    console.error(`  ✗ Failed to create device station for '${device.name}'`);
                    deviceIndex++;
                    return;
                }

                sceneData.scene.add(station.group);
                sceneData.deviceMeshes[device.id] = station.group;  // Store group (contains ApiDef cubes), not just indicator
                sceneData.devices.push(device);

                console.log(`  ✓ Device '${device.name}' (${flowName}) placed at (${x.toFixed(2)}, ${z.toFixed(2)}) with ${device.apiDefs.length} ApiDefs`);
                deviceIndex++;
                successCount++;
            });
        });

        // Save positions to localStorage
        const positions = {};
        sceneData.devices.forEach(d => {
            positions[d.id] = { x: d.posX, z: d.posZ };
        });
        this._saveDevicePositions(elementId, positions);

        // Update call cubes cache for chain visualization
        this._updateCallCubesCache(elementId);

        // Reset placing flag
        sceneData._placingAllDevices = false;

        console.log(`✓ All ${successCount}/${devices.length} devices placed successfully (${flowCount} flows)`);
    },

    /**
     * Clear all devices from the scene (removes Device groups and clears localStorage)
     * @param {string} elementId - Scene identifier
     */
    clearAllDevices: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn(`Scene '${elementId}' not found`);
            return;
        }

        console.log(`Clearing all devices from scene '${elementId}'`);

        // Clear 3D arrows and visualizations first
        this._clearCallChainVisualization(elementId);
        this._clearHighlights(elementId);

        // Find all device groups in the scene (look for deviceId in userData)
        const groupsToRemove = [];
        sceneData.scene.traverse(obj => {
            // Find top-level device groups (direct children of scene with deviceId)
            if (obj.userData && obj.userData.deviceId && obj.parent === sceneData.scene) {
                groupsToRemove.push(obj);
            }
        });

        console.log(`Found ${groupsToRemove.length} device groups to remove`);

        // Remove each device group
        groupsToRemove.forEach(group => {
            // Dispose geometry and materials recursively
            group.traverse(child => {
                if (child.geometry) child.geometry.dispose();
                if (child.material) {
                    if (Array.isArray(child.material)) {
                        child.material.forEach(m => m.dispose());
                    } else {
                        child.material.dispose();
                    }
                }
            });

            sceneData.scene.remove(group);
        });

        // Clear device meshes registry
        sceneData.deviceMeshes = {};
        sceneData.devices = [];

        // Clear selection state
        sceneData.selectedObject = null;
        sceneData.selectedCall = null;

        // Clear device positions from localStorage
        this._saveDevicePositions(elementId, {});

        // Update call cubes cache
        this._updateCallCubesCache(elementId);

        console.log(`✓ Cleared ${groupsToRemove.length} devices from scene`);
    },

    /**
     * Add a Work station at specified 3D position
     * @param {string} elementId - Scene identifier
     * @param {object} workData - Work data { id, name, flowName, state, calls, callCount, posX, posY, posZ }
     * @param {number} x - World X position
     * @param {number} z - World Z position
     */
    addWorkAtPosition: function(elementId, workData, x, z) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn(`Scene '${elementId}' not found`);
            return;
        }

        console.log(`Adding work '${workData.name}' at (${x.toFixed(2)}, ${z.toFixed(2)})`);

        // Build work object with position
        const work = {
            id: workData.id,
            name: workData.name,
            flowName: workData.flowName || 'Default',
            state: workData.state || 'R',
            posX: x,
            posY: 0,
            posZ: z,
            calls: workData.calls || [],
            callCount: workData.callCount || 0,
            devices: [],
            incoming: workData.incoming || [],
            outgoing: workData.outgoing || []
        };

        // Create work station
        const stationCount = sceneData.stationMeshes ? Object.keys(sceneData.stationMeshes).length : 0;
        const stationGroup = this._createStation(work, stationCount);
        sceneData.scene.add(stationGroup.group);

        // Register mesh
        if (!sceneData.stationMeshes) sceneData.stationMeshes = {};
        sceneData.stationMeshes[work.id] = stationGroup.indicator;

        // Save position to localStorage
        this._saveWorkPositions(elementId);

        // Update call cubes cache for chain visualization
        this._updateCallCubesCache(elementId);

        console.log(`✓ Work '${work.name}' added to scene at (${x.toFixed(2)}, ${z.toFixed(2)})`);
    },

    /**
     * Place all works at once with auto-layout
     * @param {string} elementId - Scene identifier
     * @param {Array} works - Array of work data
     * @param {Array} connections - Array of {from, to} connections
     */
    placeAllWorks: function(elementId, works, connections) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn(`Scene '${elementId}' not found`);
            return;
        }

        console.log(`Placing all ${works.length} works with ${connections.length} connections`);

        // Load saved positions or calculate new layout
        const savedPositions = this._loadWorkPositions(elementId);
        const hasPositions = savedPositions && Object.keys(savedPositions).length > 0;

        // Group works by flowName for layout calculation
        const flowGroups = {};
        works.forEach(work => {
            const flowName = work.flowName || 'Default';
            if (!flowGroups[flowName]) flowGroups[flowName] = [];
            flowGroups[flowName].push(work);
        });

        // Calculate layout positions if not saved
        const flowNames = Object.keys(flowGroups);
        const flowCount = flowNames.length;
        const spacing = 10;
        const flowSpacing = 20;

        works.forEach((work, idx) => {
            // Use saved position or calculate new
            let posX, posZ;
            if (hasPositions && savedPositions[work.id]) {
                posX = savedPositions[work.id].x;
                posZ = savedPositions[work.id].z;
            } else {
                // Calculate position based on flow grouping
                const flowName = work.flowName || 'Default';
                const flowIdx = flowNames.indexOf(flowName);
                const flowWorks = flowGroups[flowName];
                const workIdx = flowWorks.indexOf(work);
                const worksPerRow = Math.ceil(Math.sqrt(flowWorks.length));

                const row = Math.floor(workIdx / worksPerRow);
                const col = workIdx % worksPerRow;

                posX = (col - (worksPerRow - 1) / 2) * spacing + (flowIdx - (flowCount - 1) / 2) * flowSpacing;
                posZ = (row - Math.floor(flowWorks.length / worksPerRow) / 2) * spacing;
            }

            // Build work object
            const workObj = {
                id: work.id,
                name: work.name,
                flowName: work.flowName || 'Default',
                state: work.state || 'R',
                posX: posX,
                posY: 0,
                posZ: posZ,
                calls: work.calls || [],
                callCount: work.callCount || 0,
                devices: work.devices || [],
                incoming: work.incoming || [],
                outgoing: work.outgoing || []
            };

            // Create work station
            const stationGroup = this._createStation(workObj, idx);
            sceneData.scene.add(stationGroup.group);

            // Register mesh
            if (!sceneData.stationMeshes) sceneData.stationMeshes = {};
            sceneData.stationMeshes[work.id] = stationGroup.indicator;
        });

        // Create connections and store references
        sceneData.workConnectionLines = this._createConnections(sceneData.scene, connections, sceneData.stationMeshes, sceneData);

        // Calculate flow zones (bounding boxes)
        this._createFlowZonesFromWorks(sceneData.scene, works, sceneData.stationMeshes);

        // Save positions
        this._saveWorkPositions(elementId);

        // Fit camera to show all works
        this._fitCameraToScene(sceneData.scene, sceneData.camera, sceneData.controls, works);

        // Update call cubes cache for chain visualization
        this._updateCallCubesCache(elementId);

        console.log(`✓ All ${works.length} works placed successfully`);
    },

    /**
     * Create flow zones (floor rectangles) from works
     */
    _createFlowZonesFromWorks: function(scene, works, stationMeshes) {
        const flowGroups = {};
        works.forEach(work => {
            const flowName = work.flowName || 'Default';
            if (!flowGroups[flowName]) flowGroups[flowName] = [];
            flowGroups[flowName].push(work);
        });

        const flowColors = [
            0x10b981, // Green
            0x3b82f6, // Blue
            0xf59e0b, // Amber
            0xef4444, // Red
            0x8b5cf6, // Violet
            0xec4899, // Pink
            0x06b6d4, // Cyan
            0x84cc16  // Lime
        ];

        let flowIdx = 0;
        for (const flowName in flowGroups) {
            const flowWorks = flowGroups[flowName];
            if (flowWorks.length === 0) continue;

            // Find bounding box from station positions
            let minX = Infinity, maxX = -Infinity;
            let minZ = Infinity, maxZ = -Infinity;

            flowWorks.forEach(work => {
                const mesh = stationMeshes[work.id];
                if (mesh && mesh.parent) {
                    const pos = mesh.parent.position;
                    minX = Math.min(minX, pos.x);
                    maxX = Math.max(maxX, pos.x);
                    minZ = Math.min(minZ, pos.z);
                    maxZ = Math.max(maxZ, pos.z);
                }
            });

            if (minX === Infinity) continue;

            // Add padding
            const padding = 6;
            minX -= padding;
            maxX += padding;
            minZ -= padding;
            maxZ += padding;

            const centerX = (minX + maxX) / 2;
            const centerZ = (minZ + maxZ) / 2;
            const sizeX = maxX - minX;
            const sizeZ = maxZ - minZ;

            // Create floor zone
            const zoneColor = flowColors[flowIdx % flowColors.length];
            const zoneGeo = new THREE.PlaneGeometry(sizeX, sizeZ);
            const zoneMat = new THREE.MeshStandardMaterial({
                color: zoneColor,
                transparent: true,
                opacity: 0.15,
                roughness: 0.9,
                metalness: 0.0,
                side: THREE.DoubleSide
            });
            const zoneMesh = new THREE.Mesh(zoneGeo, zoneMat);
            zoneMesh.rotation.x = -Math.PI / 2;
            zoneMesh.position.set(centerX, 0.02, centerZ);
            zoneMesh.receiveShadow = true;
            zoneMesh.name = `flowZone_${flowName}`;
            zoneMesh.userData = {
                isFlowZone: true,
                flowName: flowName,
                color: zoneColor
            };
            scene.add(zoneMesh);

            // Create border
            const borderShape = new THREE.Shape();
            borderShape.moveTo(-sizeX / 2, -sizeZ / 2);
            borderShape.lineTo(sizeX / 2, -sizeZ / 2);
            borderShape.lineTo(sizeX / 2, sizeZ / 2);
            borderShape.lineTo(-sizeX / 2, sizeZ / 2);
            borderShape.lineTo(-sizeX / 2, -sizeZ / 2);

            const borderGeometry = new THREE.BufferGeometry().setFromPoints(borderShape.getPoints());
            const borderMaterial = new THREE.LineBasicMaterial({
                color: zoneColor,
                transparent: true,
                opacity: 0.6,
                linewidth: 2
            });
            const border = new THREE.Line(borderGeometry, borderMaterial);
            border.rotation.x = -Math.PI / 2;
            border.position.set(centerX, 0.03, centerZ);
            border.name = `flowZone_${flowName}_border`;
            scene.add(border);

            // Add flow name text on the ground (top-left corner of Flow Zone)
            const label = this._createFloorText(flowName, zoneColor);
            const labelX = centerX - sizeX / 2 + 3;  // Left edge + padding
            const labelZ = centerZ - sizeZ / 2 - 1;  // Top edge - outside zone boundary
            label.position.set(labelX, 0.08, labelZ);  // Elevated to be visible above zone
            label.name = `flowZone_${flowName}_label`;
            scene.add(label);

            flowIdx++;
        }
    },

    /**
     * Apply auto-layout to all devices
     * @param {string} elementId - Scene identifier
     * @param {string} layoutType - Layout type: 'grid', 'circular', 'hierarchical', 'flow'
     */
    applyAutoLayout: function(elementId, layoutType) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.devices || sceneData.devices.length === 0) {
            console.warn(`No devices to layout for scene '${elementId}'`);
            return;
        }

        console.log(`Applying '${layoutType}' layout to ${sceneData.devices.length} devices`);

        const devices = sceneData.devices;
        const count = devices.length;
        let positions = [];

        switch (layoutType.toLowerCase()) {
            case 'grid':
                positions = this._calculateGridLayout(count);
                break;
            case 'circular':
                positions = this._calculateCircularLayout(count);
                break;
            case 'hierarchical':
                positions = this._calculateHierarchicalLayout(devices);
                break;
            case 'flow':
                positions = this._calculateFlowLayout(devices);
                break;
            default:
                console.warn(`Unknown layout type: ${layoutType}`);
                positions = this._calculateGridLayout(count);
        }

        // Apply positions with animation
        this._animateDevicesToPositions(elementId, positions);
    },

    /**
     * Calculate grid layout positions
     * @param {number} count - Number of devices
     * @returns {Array} Array of {x, z} positions
     */
    _calculateGridLayout: function(count) {
        const cols = Math.ceil(Math.sqrt(count));
        const spacing = 12;
        const positions = [];

        for (let i = 0; i < count; i++) {
            const row = Math.floor(i / cols);
            const col = i % cols;
            const totalRows = Math.ceil(count / cols);
            positions.push({
                x: (col - (cols - 1) / 2) * spacing,
                z: (row - (totalRows - 1) / 2) * spacing
            });
        }

        console.log(`Grid layout: ${cols} columns, ${Math.ceil(count / cols)} rows, spacing ${spacing}`);
        return positions;
    },

    /**
     * Calculate circular layout positions
     * @param {number} count - Number of devices
     * @returns {Array} Array of {x, z} positions
     */
    _calculateCircularLayout: function(count) {
        const radius = Math.max(15, count * 3);
        const positions = [];

        for (let i = 0; i < count; i++) {
            const angle = (i / count) * Math.PI * 2 - Math.PI / 2;
            positions.push({
                x: Math.cos(angle) * radius,
                z: Math.sin(angle) * radius
            });
        }

        console.log(`Circular layout: radius ${radius}`);
        return positions;
    },

    /**
     * Calculate hierarchical layout (grouped by workId)
     * @param {Array} devices - Array of device objects
     * @returns {Array} Array of {x, z, deviceId} positions
     */
    _calculateHierarchicalLayout: function(devices) {
        // Group by workId
        const groups = {};
        devices.forEach((d, idx) => {
            const groupKey = d.workId || d.flowId || `group_${Math.floor(idx / 3)}`;
            if (!groups[groupKey]) groups[groupKey] = [];
            groups[groupKey].push({ device: d, originalIndex: idx });
        });

        const groupKeys = Object.keys(groups);
        const rowSpacing = 15;
        const colSpacing = 12;
        const positions = [];

        // Initialize positions array with nulls
        for (let i = 0; i < devices.length; i++) {
            positions.push(null);
        }

        groupKeys.forEach((groupKey, rowIdx) => {
            const groupDevices = groups[groupKey];
            const centerOffset = (groupDevices.length - 1) / 2;

            groupDevices.forEach((item, colIdx) => {
                positions[item.originalIndex] = {
                    x: (colIdx - centerOffset) * colSpacing,
                    z: (rowIdx - (groupKeys.length - 1) / 2) * rowSpacing,
                    deviceId: item.device.id
                };
            });
        });

        // Fill any remaining nulls with grid positions
        let nullCount = 0;
        positions.forEach((pos, idx) => {
            if (pos === null) {
                positions[idx] = {
                    x: (nullCount % 3) * colSpacing,
                    z: Math.floor(nullCount / 3) * rowSpacing + groupKeys.length * rowSpacing,
                    deviceId: devices[idx].id
                };
                nullCount++;
            }
        });

        console.log(`Hierarchical layout: ${groupKeys.length} groups`);
        return positions;
    },

    /**
     * Calculate flow layout (based on connections)
     * @param {Array} devices - Array of device objects
     * @returns {Array} Array of {x, z} positions
     */
    _calculateFlowLayout: function(devices) {
        // For now, use hierarchical as base with horizontal flow direction
        const positions = this._calculateHierarchicalLayout(devices);

        // Rotate 90 degrees to make it horizontal
        positions.forEach(pos => {
            if (pos) {
                const tempX = pos.x;
                pos.x = pos.z;
                pos.z = -tempX;
            }
        });

        console.log(`Flow layout applied`);
        return positions;
    },

    /**
     * Animate devices to new positions
     * @param {string} elementId - Scene identifier
     * @param {Array} targetPositions - Array of {x, z} positions
     */
    _animateDevicesToPositions: function(elementId, targetPositions) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const duration = 800; // ms
        const startTime = performance.now();

        // Get current positions
        const startPositions = sceneData.devices.map((device) => {
            const group = sceneData.deviceMeshes[device.id];  // deviceMeshes now stores groups directly
            return group ? { x: group.position.x, z: group.position.z } : { x: 0, z: 0 };
        });

        // Easing function
        const easeOutCubic = (t) => 1 - Math.pow(1 - t, 3);

        const animate = (currentTime) => {
            const elapsed = currentTime - startTime;
            const progress = Math.min(elapsed / duration, 1);
            const eased = easeOutCubic(progress);

            sceneData.devices.forEach((device, i) => {
                const group = sceneData.deviceMeshes[device.id];  // deviceMeshes now stores groups directly
                if (!group || !targetPositions[i]) return;

                const start = startPositions[i];
                const end = targetPositions[i];

                group.position.x = start.x + (end.x - start.x) * eased;
                group.position.z = start.z + (end.z - start.z) * eased;

                // Update device data
                device.posX = group.position.x;
                device.posZ = group.position.z;
            });

            if (progress < 1) {
                requestAnimationFrame(animate);
            } else {
                // Save final positions
                this._saveAllDevicePositions(elementId);
                console.log(`✓ Layout animation complete`);
            }
        };

        requestAnimationFrame(animate);
    },

    /**
     * Save all device positions to localStorage
     * @param {string} elementId - Scene identifier
     */
    _saveAllDevicePositions: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const positions = {};
        sceneData.devices.forEach(device => {
            const group = sceneData.deviceMeshes[device.id];  // deviceMeshes now stores groups directly
            if (group) {
                positions[device.id] = {
                    x: group.position.x,
                    z: group.position.z
                };
            }
        });

        this._saveDevicePositions(elementId, positions);
        console.log(`Saved ${Object.keys(positions).length} device positions`);
    },

    /**
     * Reset device layout (clear saved positions)
     * @param {string} elementId - Scene identifier
     */
    resetDeviceLayout: function(elementId) {
        console.log(`Resetting device layout for scene '${elementId}'`);

        // Clear saved positions
        try {
            const key = `ev2_device_positions_${elementId}`;
            localStorage.removeItem(key);
        } catch (e) {
            console.warn('Failed to clear device positions:', e);
        }

        // Apply default grid layout
        this.applyAutoLayout(elementId, 'grid');
    },

    // ========== AAS Tree Drag & Drop ==========

    /**
     * 화면 좌표를 3D 월드 좌표로 변환 (바닥 평면 Y=0 기준)
     * @param {string} elementId - Scene identifier
     * @param {number} clientX - Screen X coordinate
     * @param {number} clientY - Screen Y coordinate
     * @returns {number[]} [worldX, worldZ] - World coordinates on ground plane
     */
    screenToWorldPosition: function(elementId, clientX, clientY) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn('Scene not found:', elementId);
            return [0, 0];
        }

        const container = document.getElementById(elementId);
        if (!container) {
            console.warn('Container not found:', elementId);
            return [0, 0];
        }

        const rect = container.getBoundingClientRect();

        // 정규화된 디바이스 좌표 (-1 ~ 1)
        const mouse = new THREE.Vector2(
            ((clientX - rect.left) / rect.width) * 2 - 1,
            -((clientY - rect.top) / rect.height) * 2 + 1
        );

        // 바닥 평면(Y=0)과 레이캐스트
        const raycaster = new THREE.Raycaster();
        raycaster.setFromCamera(mouse, sceneData.camera);

        const groundPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
        const intersection = new THREE.Vector3();

        if (raycaster.ray.intersectPlane(groundPlane, intersection)) {
            console.log(`Screen (${clientX.toFixed(0)}, ${clientY.toFixed(0)}) → World (${intersection.x.toFixed(2)}, ${intersection.z.toFixed(2)})`);
            return [intersection.x, intersection.z];
        }

        console.warn('No intersection with ground plane');
        return [0, 0];
    },

    // =========================================================================
    // Device Drag-Drop System (from Ev2)
    // =========================================================================

    /**
     * Add a device at specified 3D position (with ApiDef cubes)
     * @param {string} elementId - Scene identifier
     * @param {object} deviceData - Device data { deviceId, deviceName, deviceType, flowName, state, apiDefs }
     * @param {number} x - World X position
     * @param {number} z - World Z position
     */
    addDeviceAtPosition: function(elementId, deviceData, x, z) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) {
            console.warn(`Scene '${elementId}' not found`);
            return;
        }

        if (!deviceData) {
            console.error('addDeviceAtPosition: deviceData is null or undefined');
            return;
        }

        const deviceName = deviceData.deviceName || deviceData.name || 'Unknown';
        const deviceId = deviceData.deviceId || deviceData.id || `device_${Date.now()}`;
        const apiDefs = Array.isArray(deviceData.apiDefs) ? deviceData.apiDefs : [];

        console.log(`Adding device '${deviceName}' at (${x.toFixed(2)}, ${z.toFixed(2)}) with ${apiDefs.length} ApiDefs`);

        // Create device object with ApiDefs
        const device = {
            id: deviceId,
            name: deviceName,
            modelType: deviceData.modelType || '',
            deviceType: deviceData.deviceType || 'general',
            flowName: deviceData.flowName || 'Default',
            state: deviceData.state || 'R',
            posX: x,
            posY: 0,
            posZ: z,
            apiDefs: apiDefs.map(a => ({
                id: a?.id || '',
                name: a?.name || '',
                guid: a?.guid || '',
                callerCount: a?.callerCount || 0,
                state: a?.state || 'R'
            }))
        };

        // Create device station
        const station = this._createDeviceStation(device, sceneData.devices.length, sceneData.scene);
        if (!station || !station.group) {
            console.error(`Failed to create device station for '${device.name}'`);
            return;
        }

        sceneData.scene.add(station.group);
        sceneData.deviceMeshes[device.id] = station.group;  // Store group (contains ApiDef cubes), not just indicator
        sceneData.devices.push(device);

        // Save position to localStorage
        const positions = this._loadDevicePositions(elementId) || {};
        positions[device.id] = { x, z };
        this._saveDevicePositions(elementId, positions);

        console.log(`✓ Device '${device.name}' added to scene with ${device.apiDefs.length} ApiDefs`);
    },

    /**
     * Create device station with ApiDef cubes
     * @param {object} device - Device data
     * @param {number} index - Device index
     * @param {THREE.Scene} scene - Scene for Flow Zone color lookup
     */
    _createDeviceStation: function(device, index, scene = null) {
        const group = new THREE.Group();
        group.position.set(device.posX || 0, device.posY || 0, device.posZ || 0);
        group.userData.deviceId = device.id;
        group.userData.deviceData = device;

        const stateColor = this.stateColors[device.state] || this.stateColors.R;

        // Get Flow Zone color for this device
        const flowColor = scene && device.flowName
            ? this._getFlowZoneColor(scene, device.flowName)
            : null;

        // Create device model: Lib3D(ModelType 기반) → 없으면 procedural fallback
        let model;
        let modelHeight = 2;
        let isRobotType = false;

        const lib = window.Ds2View3DLibrary;
        if (!lib) {
            console.warn(`[3D] Ds2View3DLibrary not available for '${device.name}'`);
        } else if (!device.modelType) {
            console.warn(`[3D] modelType empty for '${device.name}' (deviceType=${device.deviceType})`);
        } else {
            try {
                const info = lib.getDeviceInfo(device.modelType);
                const libModel = lib.createSync(device.modelType, THREE);
                if (libModel) {
                    console.log(`[3D] ✅ Lib3D model '${device.modelType}' loaded for '${device.name}'`);
                    model = libModel;
                    modelHeight = info.height;
                    isRobotType = device.modelType.startsWith('Robot');
                } else {
                    console.warn(`[3D] ⚠️ createSync returned null for '${device.modelType}' (${device.name})`);
                }
            } catch (e) {
                console.error(`[3D] ❌ Lib3D model creation failed for '${device.modelType}': ${e.message}`);
            }
        }

        // Fallback: SystemType 미매핑 시 coarse deviceType 기반 procedural 모델
        if (!model) {
            const dt = device.deviceType || 'general';
            if (dt === 'robot') {
                model = this._createRobotModel(1.0, stateColor);
                modelHeight = 3;
                isRobotType = true;
            } else if (dt === 'small' || dt === 'cylinder') {
                model = this._createSmallDeviceModel(1.0, stateColor);
                modelHeight = 1.5;
            } else {
                model = this._createGeneralDeviceModel(1.0, stateColor);
                modelHeight = 2;
            }
        }

        model.userData.deviceId = device.id;
        model.userData.deviceData = device;
        model.userData.isDeviceIndicator = true;
        model.userData.isRobot = isRobotType;
        model.userData.modelType3D = device.modelType || '';  // Lib3D class name for animate()
        group.add(model);

        // ApiDef cubes on top of device (with Flow Zone color)
        if (device.apiDefs && device.apiDefs.length > 0) {
            const apiDefSpacing = 1.0;
            const startX = -(device.apiDefs.length - 1) * apiDefSpacing / 2;
            const apiDefY = modelHeight + 0.5;

            device.apiDefs.forEach((apiDef, apiIdx) => {
                const cube = this._createApiDefCube(apiDef, device.id, flowColor);
                cube.position.set(startX + apiIdx * apiDefSpacing, apiDefY, 0);
                cube.userData.pulseOffset = index * 0.5 + apiIdx * 0.3;
                cube.userData.apiDefIdx = apiIdx;  // 0-based index for direction lookup
                group.add(cube);
            });
        }

        // Device name label
        const labelY = modelHeight + (device.apiDefs?.length > 0 ? 2.0 : 1.0);
        const label = this._createLabel(device.name || device.id);
        label.position.set(0, labelY, 0);
        label.userData.deviceId = device.id;
        group.add(label);

        return { group, indicator: model };
    },

    /**
     * Create ApiDef cube with caller count badge
     * @param {object} apiDef - ApiDef data
     * @param {string} deviceId - Parent device ID
     * @param {number|null} flowColor - Optional Flow Zone color (hex) for blending
     */
    _createApiDefCube: function(apiDef, deviceId, flowColor = null) {
        const stateColor = this.stateColors[apiDef.state] || this.stateColors.R;
        const cubeSize = 0.5;

        // Blend state color with flow color if available
        let finalColor = stateColor.hex;
        if (flowColor !== null) {
            // Mix state color (60%) with flow color (40%) for visual distinction
            const stateR = (stateColor.hex >> 16) & 0xff;
            const stateG = (stateColor.hex >> 8) & 0xff;
            const stateB = stateColor.hex & 0xff;

            const flowR = (flowColor >> 16) & 0xff;
            const flowG = (flowColor >> 8) & 0xff;
            const flowB = flowColor & 0xff;

            const mixR = Math.round(stateR * 0.6 + flowR * 0.4);
            const mixG = Math.round(stateG * 0.6 + flowG * 0.4);
            const mixB = Math.round(stateB * 0.6 + flowB * 0.4);

            finalColor = (mixR << 16) | (mixG << 8) | mixB;
        }

        const cubeGeo = new THREE.BoxGeometry(cubeSize, cubeSize, cubeSize);
        const cubeMat = new THREE.MeshStandardMaterial({
            color: finalColor,
            emissive: finalColor,
            emissiveIntensity: 0.4,
            metalness: 0.5,
            roughness: 0.3
        });
        const cube = new THREE.Mesh(cubeGeo, cubeMat);

        cube.userData.apiDefId = apiDef.id;
        cube.userData.apiDefData = apiDef;
        cube.userData.deviceId = deviceId;
        cube.userData.isApiDefCube = true;
        cube.userData.flowColor = flowColor; // Store for updates

        // Caller count badge (if > 1)
        if (apiDef.callerCount > 1) {
            const badge = this._createCallerCountBadge(apiDef.callerCount);
            badge.position.set(cubeSize / 2 + 0.1, cubeSize / 2 + 0.1, 0);
            cube.add(badge);
        }

        cube.castShadow = true;
        return cube;
    },

    /**
     * Create caller count badge sprite
     */
    _createCallerCountBadge: function(count) {
        const canvas = document.createElement('canvas');
        canvas.width = 64;
        canvas.height = 64;
        const ctx = canvas.getContext('2d');

        // Red circle background
        ctx.fillStyle = '#ef4444';
        ctx.beginPath();
        ctx.arc(32, 32, 26, 0, Math.PI * 2);
        ctx.fill();

        // White border
        ctx.strokeStyle = '#ffffff';
        ctx.lineWidth = 2;
        ctx.stroke();

        // Count number
        ctx.fillStyle = '#ffffff';
        ctx.font = 'bold 28px Arial';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(count.toString(), 32, 32);

        const texture = new THREE.CanvasTexture(canvas);
        const spriteMat = new THREE.SpriteMaterial({ map: texture, transparent: true });
        const sprite = new THREE.Sprite(spriteMat);
        sprite.scale.set(0.4, 0.4, 1);

        return sprite;
    },

    /**
     * Save device positions to localStorage
     */
    _saveDevicePositions: function(elementId, positions) {
        const key = `ds2_device_positions_${elementId}`;
        try {
            localStorage.setItem(key, JSON.stringify(positions));
            console.log(`✓ Saved ${Object.keys(positions).length} device positions to localStorage`);
        } catch (e) {
            console.warn('Failed to save device positions:', e);
        }
    },

    /**
     * Load device positions from localStorage
     */
    _loadDevicePositions: function(elementId) {
        const key = `ds2_device_positions_${elementId}`;
        try {
            const data = localStorage.getItem(key);
            if (data) {
                const positions = JSON.parse(data);
                console.log(`✓ Loaded ${Object.keys(positions).length} device positions from localStorage`);
                return positions;
            }
        } catch (e) {
            console.warn('Failed to load device positions:', e);
        }
        return {};
    },

    /**
     * Get stored device positions (for Blazor interop)
     */
    getStoredDevicePositions: function(elementId) {
        return this._loadDevicePositions(elementId);
    },

    // REMOVED: Duplicate setEditMode function - already defined at line 623

    /**
     * Fit camera to show all devices in view
     * @param {string} elementId - Scene identifier
     */
    fitAll: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.camera || !sceneData.controls) {
            console.warn(`Scene '${elementId}' not ready for fitAll`);
            return;
        }

        // Calculate bounding box of all devices
        const boundingBox = new THREE.Box3();

        if (sceneData.deviceMeshes) {
            Object.values(sceneData.deviceMeshes).forEach(mesh => {
                if (mesh && mesh.geometry) {
                    boundingBox.expandByObject(mesh);
                }
            });
        }

        // If no devices, return
        if (boundingBox.isEmpty()) {
            console.warn('No devices to fit in view');
            return;
        }

        // Calculate center and size
        const center = boundingBox.getCenter(new THREE.Vector3());
        const size = boundingBox.getSize(new THREE.Vector3());
        const maxDim = Math.max(size.x, size.z);

        // Calculate camera distance
        const fov = sceneData.camera.fov * (Math.PI / 180);
        const distance = Math.abs(maxDim / (2 * Math.tan(fov / 2))) * 1.5; // 1.5x for padding

        // Set camera position
        sceneData.camera.position.set(
            center.x + distance * 0.7,
            distance * 0.7,
            center.z + distance * 0.7
        );

        // Update controls target
        sceneData.controls.target.copy(center);
        sceneData.controls.update();

        console.log('✓ Camera fitted to show all devices');
    },

    /**
     * Select device by name (from tree)
     * @param {string} elementId - Scene identifier
     * @param {string} deviceName - Device name to select
     */
    selectDeviceByName: function(elementId, deviceName) {
        try {
            const sceneData = this._scenes[elementId];
            if (!sceneData) {
                console.warn(`[selectDeviceByName] scene '${elementId}' not found`);
                return;
            }

            console.log(`[selectDeviceByName] looking for '${deviceName}' in ${sceneData.devices?.length || 0} devices`);

            // Find device by name
            const device = sceneData.devices?.find(d => d.name === deviceName);
            if (!device) {
                console.warn(`[selectDeviceByName] Device '${deviceName}' not found. Available: [${(sceneData.devices || []).map(d => d.name).join(', ')}]`);
                return;
            }

            // Find device mesh
            const deviceMesh = sceneData.deviceMeshes[device.id];
            if (!deviceMesh) return;

            // Clear existing highlights and ApiDef connections
            this._clearHighlights(elementId);
            this._clearApiDefConnections(elementId);
            sceneData.selectedApiDef = null;

            // Highlight selected device
            sceneData.selectedObject = device.id;
            this._highlightDevice(elementId, deviceMesh);

            // Move camera to device
            const pos = deviceMesh.position;
            const camera = sceneData.camera;
            const controls = sceneData.controls;

            if (camera && controls) {
                controls.target.set(pos.x, 0, pos.z);
                controls.update();
                console.log(`✓ Selected device '${deviceName}' in 3D view`);
            }
        } catch (e) {
            console.error(`selectDeviceByName error for '${deviceName}':`, e);
        }
    },

    // REMOVED: setFlowEditMode, toggleFlowEditMode, _makeFlowZonesInteractive
    // Flow Zone editing is now automatic based on device positions

    /**
     * Recalculate all Flow Zone label positions based on current Flow Zone positions and sizes
     * @param {string} elementId - Scene identifier
     */
    _recalculateFlowZoneLabels: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.scene) return;

        let recalculatedCount = 0;
        let flowZoneCount = 0;
        let labelNotFoundCount = 0;

        console.log('[DEBUG] Starting Flow Zone label recalculation...');

        // Find all Flow Zone meshes
        sceneData.scene.traverse((obj) => {
            if (obj.userData?.isFlowZone && obj.name && obj.name.startsWith('flowZone_')) {
                flowZoneCount++;
                const flowZone = obj;
                const labelName = `${flowZone.name}_label`;

                console.log(`[DEBUG] Flow Zone found: ${flowZone.name}`);
                console.log(`[DEBUG]   Position: (${flowZone.position.x.toFixed(2)}, ${flowZone.position.z.toFixed(2)})`);
                console.log(`[DEBUG]   Looking for label: ${labelName}`);

                const label = sceneData.scene.getObjectByName(labelName);

                if (!flowZone.geometry || !flowZone.geometry.parameters) {
                    console.warn(`[DEBUG]   Flow Zone geometry.parameters missing!`);
                    return;
                }

                // Get current Flow Zone position and size
                const centerX = flowZone.position.x;
                const centerZ = flowZone.position.z;
                const width = flowZone.geometry.parameters.width;
                const height = flowZone.geometry.parameters.height;

                // Calculate label position (top-left corner of Flow Zone, on ground)
                const labelX = centerX - width / 2 + 3;  // Left edge + padding
                const labelZ = centerZ - height / 2 - 1;  // Top edge - outside zone boundary

                console.log(`[DEBUG]   Size: ${width.toFixed(2)} x ${height.toFixed(2)}`);
                console.log(`[DEBUG]   Target label position: (${labelX.toFixed(2)}, ${labelZ.toFixed(2)})`);

                if (label) {
                    console.log(`[DEBUG]   Label FOUND at: (${label.position.x.toFixed(2)}, ${label.position.z.toFixed(2)})`);

                    // Update existing label position
                    label.position.set(labelX, 0.08, labelZ);
                    recalculatedCount++;
                } else {
                    console.warn(`[DEBUG]   Label NOT FOUND: ${labelName} - Creating new label`);

                    // Create missing label (floor text)
                    const flowName = flowZone.userData?.flowName || flowZone.name.replace('flowZone_', '');
                    const color = flowZone.userData?.color || 0xffffff;
                    const newLabel = this._createFloorText(flowName, color);
                    newLabel.position.set(labelX, 0.08, labelZ);
                    newLabel.name = labelName;
                    sceneData.scene.add(newLabel);

                    console.log(`[DEBUG]   Created new label: ${labelName}`);
                    recalculatedCount++;
                    labelNotFoundCount++;
                }
            }
        });

        console.log(`[DEBUG] Flow Zone label recalculation complete:`);
        console.log(`[DEBUG]   Flow Zones found: ${flowZoneCount}`);
        console.log(`[DEBUG]   Labels recalculated: ${recalculatedCount}`);
        console.log(`[DEBUG]   Labels not found: ${labelNotFoundCount}`);

        if (recalculatedCount > 0) {
            console.log(`✓ Recalculated ${recalculatedCount} Flow Zone label positions`);
        } else if (flowZoneCount > 0) {
            console.warn(`⚠ Found ${flowZoneCount} Flow Zones but no labels were updated!`);
        }
    },

    /**
     * Delete selected Flow Zone
     * @param {string} elementId - Scene identifier
     */
    deleteSelectedFlowZone: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.flowEditMode || !sceneData.selectedFlowZone) {
            console.warn('No Flow Zone selected or not in edit mode');
            return;
        }

        const flowZoneMesh = sceneData.selectedFlowZone;
        const flowName = flowZoneMesh.userData.flowName;

        // Remove from scene
        if (flowZoneMesh.parent) {
            flowZoneMesh.parent.remove(flowZoneMesh);
        }

        // Remove border if exists
        const borderName = `${flowZoneMesh.name}_border`;
        const border = sceneData.scene.getObjectByName(borderName);
        if (border && border.parent) {
            border.parent.remove(border);
        }

        // Remove label if exists
        const labelName = `${flowZoneMesh.name}_label`;
        const label = sceneData.scene.getObjectByName(labelName);
        if (label && label.parent) {
            label.parent.remove(label);
        }

        sceneData.selectedFlowZone = null;
        console.log(`✓ Deleted Flow Zone: ${flowName}`);
    },

    /**
     * Change color of selected Flow Zone
     * @param {string} elementId - Scene identifier
     */
    changeFlowZoneColor: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.flowEditMode || !sceneData.selectedFlowZone) {
            console.warn('No Flow Zone selected or not in edit mode');
            return;
        }

        // Predefined colors
        const colors = [
            0x3b82f6, // Blue
            0x10b981, // Green
            0xf59e0b, // Amber
            0xef4444, // Red
            0x8b5cf6, // Purple
            0xec4899, // Pink
            0x06b6d4, // Cyan
            0x84cc16  // Lime
        ];

        const flowZoneMesh = sceneData.selectedFlowZone;
        const currentColor = flowZoneMesh.material.color.getHex();

        // Find next color in cycle
        let currentIndex = colors.findIndex(c => c === currentColor);
        const nextColor = colors[(currentIndex + 1) % colors.length];

        // Update mesh color
        flowZoneMesh.material.color.setHex(nextColor);
        flowZoneMesh.userData.color = nextColor;

        // Update border color
        const borderName = `${flowZoneMesh.name}_border`;
        const border = sceneData.scene.getObjectByName(borderName);
        if (border) {
            border.material.color.setHex(nextColor);
        }

        console.log(`✓ Changed Flow Zone color to #${nextColor.toString(16).padStart(6, '0')}`);
    },

    /**
     * Update Flow Zone highlights
     * @param {string} elementId - Scene identifier
     */
    _updateFlowZoneHighlights: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.scene) return;

        // Clear all highlights
        sceneData.scene.traverse((obj) => {
            if (obj.userData.isFlowZone && obj.material) {
                obj.material.opacity = 0.15;
            }
            if (obj.name && obj.name.endsWith('_border') && obj.material) {
                obj.material.opacity = 0.6;
            }
        });

        // Highlight selected
        if (sceneData.selectedFlowZone) {
            sceneData.selectedFlowZone.material.opacity = 0.3;
            const borderName = `${sceneData.selectedFlowZone.name}_border`;
            const border = sceneData.scene.getObjectByName(borderName);
            if (border) {
                border.material.opacity = 1.0;
            }
        }
    },

    /**
     * Detect if clicking near edge/corner for resize
     * @param {THREE.Mesh} flowZone - Flow Zone mesh
     * @param {THREE.Vector3} hitPoint - Click point
     * @returns {string|null} - Resize mode ('n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw', or null)
     */
    _getFlowZoneResizeMode: function(flowZone, hitPoint) {
        const threshold = 3; // Distance threshold for edge detection
        const width = flowZone.geometry.parameters.width;
        const height = flowZone.geometry.parameters.height;
        const centerX = flowZone.position.x;
        const centerZ = flowZone.position.z;

        const localX = hitPoint.x - centerX;
        const localZ = hitPoint.z - centerZ;

        const nearLeft = Math.abs(localX + width / 2) < threshold;
        const nearRight = Math.abs(localX - width / 2) < threshold;
        const nearTop = Math.abs(localZ - height / 2) < threshold;
        const nearBottom = Math.abs(localZ + height / 2) < threshold;

        // Corner detection (priority)
        if (nearTop && nearLeft) return 'nw';
        if (nearTop && nearRight) return 'ne';
        if (nearBottom && nearLeft) return 'sw';
        if (nearBottom && nearRight) return 'se';

        // Edge detection
        if (nearTop) return 'n';
        if (nearBottom) return 's';
        if (nearLeft) return 'w';
        if (nearRight) return 'e';

        return null; // Center - move mode
    },

    /**
     * Get cursor style for resize mode
     * @param {string} resizeMode - Resize direction
     * @returns {string} - CSS cursor style
     */
    _getResizeCursor: function(resizeMode) {
        switch (resizeMode) {
            case 'n':
            case 's':
                return 'ns-resize';
            case 'e':
            case 'w':
                return 'ew-resize';
            case 'ne':
            case 'sw':
                return 'nesw-resize';
            case 'nw':
            case 'se':
                return 'nwse-resize';
            default:
                return 'move';
        }
    },

    /**
     * Resize Flow Zone during drag
     * @param {string} elementId - Scene identifier
     * @param {THREE.Vector3} currentPoint - Current mouse position
     */
    _resizeFlowZone: function(elementId, currentPoint) {
        const sceneData = this._scenes[elementId];
        const dragState = sceneData.dragState;
        const flowZone = dragState.dragObject;

        if (!flowZone || !dragState.flowZoneOriginalSize || !dragState.flowZoneOriginalCenter) return;

        const mode = dragState.flowZoneResizeMode;
        const originalCenter = dragState.flowZoneOriginalCenter;
        const originalWidth = dragState.flowZoneOriginalSize.x;
        const originalHeight = dragState.flowZoneOriginalSize.z;

        const deltaX = currentPoint.x - dragState.flowZoneDragStart.x;
        const deltaZ = currentPoint.z - dragState.flowZoneDragStart.z;

        let newWidth = originalWidth;
        let newHeight = originalHeight;
        let newCenterX = originalCenter.x;
        let newCenterZ = originalCenter.z;

        // Calculate new dimensions based on resize mode
        switch (mode) {
            case 'e': // East (right edge)
                newWidth = Math.max(5, originalWidth + deltaX * 2);
                newCenterX = originalCenter.x + deltaX;
                break;
            case 'w': // West (left edge)
                newWidth = Math.max(5, originalWidth - deltaX * 2);
                newCenterX = originalCenter.x + deltaX;
                break;
            case 'n': // North (top edge)
                newHeight = Math.max(5, originalHeight + deltaZ * 2);
                newCenterZ = originalCenter.z + deltaZ;
                break;
            case 's': // South (bottom edge)
                newHeight = Math.max(5, originalHeight - deltaZ * 2);
                newCenterZ = originalCenter.z + deltaZ;
                break;
            case 'ne': // North-East corner
                newWidth = Math.max(5, originalWidth + deltaX * 2);
                newHeight = Math.max(5, originalHeight + deltaZ * 2);
                newCenterX = originalCenter.x + deltaX;
                newCenterZ = originalCenter.z + deltaZ;
                break;
            case 'nw': // North-West corner
                newWidth = Math.max(5, originalWidth - deltaX * 2);
                newHeight = Math.max(5, originalHeight + deltaZ * 2);
                newCenterX = originalCenter.x + deltaX;
                newCenterZ = originalCenter.z + deltaZ;
                break;
            case 'se': // South-East corner
                newWidth = Math.max(5, originalWidth + deltaX * 2);
                newHeight = Math.max(5, originalHeight - deltaZ * 2);
                newCenterX = originalCenter.x + deltaX;
                newCenterZ = originalCenter.z + deltaZ;
                break;
            case 'sw': // South-West corner
                newWidth = Math.max(5, originalWidth - deltaX * 2);
                newHeight = Math.max(5, originalHeight - deltaZ * 2);
                newCenterX = originalCenter.x + deltaX;
                newCenterZ = originalCenter.z + deltaZ;
                break;
        }

        // Update geometry
        flowZone.geometry.dispose();
        flowZone.geometry = new THREE.PlaneGeometry(newWidth, newHeight);
        flowZone.position.set(newCenterX, 0.05, newCenterZ);

        // Update border
        const borderName = `${flowZone.name}_border`;
        const border = sceneData.scene.getObjectByName(borderName);
        if (border) {
            border.geometry.dispose();
            const borderShape = new THREE.Shape();
            borderShape.moveTo(-newWidth / 2, -newHeight / 2);
            borderShape.lineTo(newWidth / 2, -newHeight / 2);
            borderShape.lineTo(newWidth / 2, newHeight / 2);
            borderShape.lineTo(-newWidth / 2, newHeight / 2);
            borderShape.lineTo(-newWidth / 2, -newHeight / 2);
            border.geometry = new THREE.BufferGeometry().setFromPoints(borderShape.getPoints());
            border.position.copy(flowZone.position);
        }

        // Update label position
        const labelName = `${flowZone.name}_label`;
        const label = sceneData.scene.getObjectByName(labelName);
        if (label) {
            label.position.set(newCenterX, 0.1, newCenterZ - newHeight / 2 - 1);
        }
    },

    // REMOVED: _createNewFlowZone - newZone creation feature disabled
    // Only existing Flow Zones from the model can be edited
    /*
    _createNewFlowZone: function(elementId, centerX, centerZ, width, height) {
        // Function removed - newZone creation not allowed
    },
    */

    /**
     * Auto-recalculate Flow Zones based on current device/work positions
     * @param {string} elementId - Scene identifier
     */
    _recalculateFlowZones: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData || !sceneData.scene) return;

        console.log('[AUTO FLOW] Recalculating Flow Zones based on device positions...');

        // Group works/devices by flow
        const flowGroups = {};

        // Collect from stationMeshes (Works)
        if (sceneData.stationMeshes) {
            Object.entries(sceneData.stationMeshes).forEach(([workId, mesh]) => {
                const workData = sceneData.works?.find(w => w.id === workId);
                if (workData && workData.flowName) {
                    const flowName = workData.flowName;
                    if (!flowGroups[flowName]) {
                        flowGroups[flowName] = [];
                    }
                    const group = mesh.parent;
                    if (group) {
                        flowGroups[flowName].push({
                            x: group.position.x,
                            z: group.position.z
                        });
                    }
                }
            });
        }

        // Collect from deviceMeshes (Devices)
        if (sceneData.deviceMeshes && sceneData.devices) {
            Object.entries(sceneData.deviceMeshes).forEach(([deviceId, group]) => {
                const deviceData = sceneData.devices.find(d => d.id === deviceId);
                if (deviceData && deviceData.flowName) {
                    const flowName = deviceData.flowName;
                    if (!flowGroups[flowName]) {
                        flowGroups[flowName] = [];
                    }
                    // deviceMeshes stores the group directly, not a child mesh
                    flowGroups[flowName].push({
                        x: group.position.x,
                        z: group.position.z
                    });
                }
            });
        }

        console.log(`[AUTO FLOW] Found ${Object.keys(flowGroups).length} active flows with devices`);

        // Track which zones we've updated
        const updatedZones = new Set();
        let updateCount = 0;

        // Update or create Flow Zones
        for (const [flowName, positions] of Object.entries(flowGroups)) {
            if (positions.length === 0) continue;

            // Calculate bounding box with adaptive padding based on zone size
            const basePadding = 6;
            const xs = positions.map(p => p.x);
            const zs = positions.map(p => p.z);
            const minX = Math.min(...xs) - basePadding;
            const maxX = Math.max(...xs) + basePadding;
            const minZ = Math.min(...zs) - basePadding;
            const maxZ = Math.max(...zs) + basePadding;

            const centerX = (minX + maxX) / 2;
            const centerZ = (minZ + maxZ) / 2;
            const sizeX = maxX - minX;
            const sizeZ = maxZ - minZ;

            // Find existing Flow Zone
            const zoneName = `flowZone_${flowName}`;
            const existingZone = sceneData.scene.getObjectByName(zoneName);

            if (existingZone) {
                // Check if zone actually changed (avoid unnecessary updates)
                const oldCenterX = existingZone.position.x;
                const oldCenterZ = existingZone.position.z;
                const oldSizeX = existingZone.geometry.parameters.width;
                const oldSizeZ = existingZone.geometry.parameters.height;

                const threshold = 0.1; // Minimum change to trigger update
                const centerChanged = Math.abs(centerX - oldCenterX) > threshold ||
                                     Math.abs(centerZ - oldCenterZ) > threshold;
                const sizeChanged = Math.abs(sizeX - oldSizeX) > threshold ||
                                   Math.abs(sizeZ - oldSizeZ) > threshold;

                if (centerChanged || sizeChanged) {
                    // Update existing Flow Zone
                    console.log(`[AUTO FLOW] Updating ${flowName}: (${centerX.toFixed(1)}, ${centerZ.toFixed(1)}) size (${sizeX.toFixed(1)} × ${sizeZ.toFixed(1)})`);

                    // Update geometry
                    existingZone.geometry.dispose();
                    existingZone.geometry = new THREE.PlaneGeometry(sizeX, sizeZ);
                    existingZone.position.set(centerX, 0.02, centerZ);

                    // Update border
                    const borderName = `${zoneName}_border`;
                    const border = sceneData.scene.getObjectByName(borderName);
                    if (border) {
                        border.geometry.dispose();
                        const borderShape = new THREE.Shape();
                        borderShape.moveTo(-sizeX / 2, -sizeZ / 2);
                        borderShape.lineTo(sizeX / 2, -sizeZ / 2);
                        borderShape.lineTo(sizeX / 2, sizeZ / 2);
                        borderShape.lineTo(-sizeX / 2, sizeZ / 2);
                        borderShape.lineTo(-sizeX / 2, -sizeZ / 2);
                        border.geometry = new THREE.BufferGeometry().setFromPoints(borderShape.getPoints());
                        border.position.copy(existingZone.position);
                    }

                    // Update label (top-left corner of Flow Zone, on ground)
                    const labelName = `${zoneName}_label`;
                    const label = sceneData.scene.getObjectByName(labelName);
                    if (label) {
                        const labelX = centerX - sizeX / 2 + 3;  // Left edge + padding
                        const labelZ = centerZ - sizeZ / 2 - 1;  // Top edge - outside zone boundary
                        label.position.set(labelX, 0.08, labelZ);
                    }

                    updateCount++;
                } else {
                    console.log(`[AUTO FLOW] ${flowName}: No significant change, skipping update`);
                }

                updatedZones.add(zoneName);
            }
        }

        // Cleanup: Hide or dim Flow Zones that have no devices
        // (We don't delete them as they may be referenced in the model)
        const allFlowZones = [];
        sceneData.scene.traverse((obj) => {
            if (obj.userData?.isFlowZone && obj.name && obj.name.startsWith('flowZone_')) {
                allFlowZones.push(obj);
            }
        });

        let hiddenCount = 0;
        allFlowZones.forEach(zone => {
            if (!updatedZones.has(zone.name)) {
                // This zone has no devices - make it semi-transparent
                if (zone.material.opacity > 0.2) {
                    zone.material.opacity = 0.15;
                    console.log(`[AUTO FLOW] Dimming empty zone: ${zone.name}`);

                    // Also dim the label
                    const labelName = `${zone.name}_label`;
                    const label = sceneData.scene.getObjectByName(labelName);
                    if (label && label.material.opacity > 0.2) {
                        label.material.opacity = 0.3;
                    }

                    hiddenCount++;
                }
            } else {
                // Restore full opacity if it was dimmed
                if (zone.material.opacity < 0.3) {
                    zone.material.opacity = 0.35;

                    const labelName = `${zone.name}_label`;
                    const label = sceneData.scene.getObjectByName(labelName);
                    if (label) {
                        label.material.opacity = 0.95;
                    }
                }
            }
        });

        console.log(`[AUTO FLOW] Recalculation complete: ${updateCount} zones updated, ${hiddenCount} zones dimmed`);
    },

    /**
     * Debounced version of Flow Zone recalculation for performance optimization
     * Delays recalculation to avoid excessive updates during rapid device movements
     * @param {string} elementId - Scene identifier
     * @param {number} delay - Debounce delay in milliseconds (default: 150ms)
     */
    _recalculateFlowZonesDebounced: function(elementId, delay = 150) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        // Clear existing timer
        if (sceneData.flowRecalcTimer) {
            clearTimeout(sceneData.flowRecalcTimer);
        }

        // Set new timer
        sceneData.flowRecalcTimer = setTimeout(() => {
            this._recalculateFlowZones(elementId);
            sceneData.flowRecalcTimer = null;
        }, delay);
    },

    /**
     * Get Flow Zone color by flowName
     * @param {THREE.Scene} scene - The scene to search
     * @param {string} flowName - Flow name to find
     * @returns {number|null} - Flow Zone color as hex number, or null if not found
     */
    _getFlowZoneColor: function(scene, flowName) {
        if (!scene || !flowName) return null;

        const zoneName = `flowZone_${flowName}`;
        const flowZone = scene.getObjectByName(zoneName);

        if (flowZone && flowZone.userData && flowZone.userData.color) {
            const color = flowZone.userData.color;
            // Convert string color to number if needed
            if (typeof color === 'string' && color.startsWith('#')) {
                return parseInt(color.slice(1), 16);
            } else if (typeof color === 'number') {
                return color;
            }
        }

        return null;
    },

    /**
     * Save Flow Zone positions to localStorage
     * @param {string} elementId - Scene identifier
     */
    _saveFlowZonePositions: function(elementId) {
        const sceneData = this._scenes[elementId];
        if (!sceneData) return;

        const flowZones = [];
        sceneData.scene.traverse((obj) => {
            if (obj.userData?.isFlowZone && obj.name && obj.name.startsWith('flowZone_')) {
                flowZones.push({
                    flowName: obj.userData.flowName,
                    centerX: obj.position.x,
                    centerZ: obj.position.z,
                    sizeX: obj.geometry.parameters.width,
                    sizeZ: obj.geometry.parameters.height,
                    color: obj.userData.color || 0x888888
                });
            }
        });

        const storageKey = `ev2-3d-flowzones-${elementId}`;
        try {
            const data = JSON.stringify(flowZones);
            localStorage.setItem(storageKey, data);
            console.log(`✓ Saved ${flowZones.length} Flow Zones (${data.length} bytes)`);
        } catch (error) {
            console.error('Failed to save Flow Zone positions:', error);
        }
    }
};

// Expose to window for Blazor interop
window.Ev23DViewer = Ev23DViewer;
