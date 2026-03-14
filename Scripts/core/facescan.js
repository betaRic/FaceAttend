/**
 * FaceAttend Core - Face Scanner
 * Unified face scanning with diversity-aware capture
 * 
 * @version 1.0.0
 * @requires ES5+
 * @requires FaceAttend.Camera
 * @requires FaceAttend.API
 * @requires FaceAttend.Notify (optional)
 */
(function(window) {
    'use strict';

    /**
     * Face scanner for enrollment and attendance
     */
    var FaceScan = {
        // Configuration
        config: {
            minFrames: 3,
            maxFrames: 8,
            livenessThreshold: 0.75,
            intervalMs: 300,
            captureWidth: 640,
            captureHeight: 480,
            jpegQuality: 0.85,
            enableDiversity: true,
            requiredBuckets: ['center', 'left', 'right', 'up', 'down']
        },
        
        // State
        state: {
            isScanning: false,
            isPaused: false,
            capturedFrames: [],
            capturedBuckets: {},
            currentBucket: null,
            timer: null,
            camera: null,
            callbacks: {
                onStart: null,
                onProgress: null,
                onFrameCapture: null,
                onComplete: null,
                onError: null,
                onPause: null,
                onResume: null
            }
        },
        
        /**
         * Initialize scanner
         * 
         * @param {Object} options - Configuration options
         * @returns {FaceScan} This instance for chaining
         */
        init: function(options) {
            // Reset state
            this.reset();
            
            // Merge options
            if (options) {
                Object.keys(options).forEach(function(key) {
                    if (this.config.hasOwnProperty(key)) {
                        this.config[key] = options[key];
                    }
                }.bind(this));
            }
            
            return this;
        },
        
        /**
         * Reset scanner state
         */
        reset: function() {
            this.stop();
            this.state.capturedFrames = [];
            this.state.capturedBuckets = {};
            this.state.currentBucket = null;
            this.state.isPaused = false;
        },
        
        /**
         * Start scanning
         * 
         * @param {Object} camera - Camera instance (FaceAttend.Camera)
         * @param {Object} callbacks - Callback functions
         */
        start: function(camera, callbacks) {
            if (this.state.isScanning) {
                console.warn('[FaceScan] Already scanning');
                return;
            }
            
            if (!camera || !camera.isActive || !camera.isActive()) {
                this._triggerError('Camera not active');
                return;
            }
            
            // Store references
            this.state.camera = camera;
            this.state.callbacks = {
                onStart: callbacks && callbacks.onStart || null,
                onProgress: callbacks && callbacks.onProgress || null,
                onFrameCapture: callbacks && callbacks.onFrameCapture || null,
                onComplete: callbacks && callbacks.onComplete || null,
                onError: callbacks && callbacks.onError || null,
                onPause: callbacks && callbacks.onPause || null,
                onResume: callbacks && callbacks.onResume || null
            };
            
            this.state.isScanning = true;
            this.state.isPaused = false;
            
            // Trigger start callback
            if (this.state.callbacks.onStart) {
                this.state.callbacks.onStart({
                    minFrames: this.config.minFrames,
                    maxFrames: this.config.maxFrames
                });
            }
            
            // Start capture loop
            var self = this;
            this.state.timer = setInterval(function() {
                if (!self.state.isPaused) {
                    self._captureFrame();
                }
            }, this.config.intervalMs);
        },
        
        /**
         * Stop scanning
         */
        stop: function() {
            this.state.isScanning = false;
            this.state.isPaused = false;
            
            if (this.state.timer) {
                clearInterval(this.state.timer);
                this.state.timer = null;
            }
            
            this.state.camera = null;
        },
        
        /**
         * Pause scanning temporarily
         */
        pause: function() {
            if (!this.state.isScanning) return;
            
            this.state.isPaused = true;
            
            if (this.state.callbacks.onPause) {
                this.state.callbacks.onPause();
            }
        },
        
        /**
         * Resume scanning
         */
        resume: function() {
            if (!this.state.isScanning) return;
            
            this.state.isPaused = false;
            
            if (this.state.callbacks.onResume) {
                this.state.callbacks.onResume();
            }
        },
        
        /**
         * Check if currently scanning
         * @returns {boolean}
         */
        isScanning: function() {
            return this.state.isScanning;
        },
        
        /**
         * Check if paused
         * @returns {boolean}
         */
        isPaused: function() {
            return this.state.isPaused;
        },
        
        /**
         * Get current progress
         * @returns {Object}
         */
        getProgress: function() {
            var capturedCount = this.state.capturedFrames.length;
            var bucketCount = Object.keys(this.state.capturedBuckets).length;
            
            return {
                current: capturedCount,
                target: this.config.minFrames,
                max: this.config.maxFrames,
                percentage: Math.min(100, Math.round((capturedCount / this.config.minFrames) * 100)),
                buckets: Object.keys(this.state.capturedBuckets),
                bucketCount: bucketCount,
                hasEnoughFrames: capturedCount >= this.config.minFrames,
                hasAllBuckets: bucketCount >= this.config.requiredBuckets.length
            };
        },
        
        /**
         * Get captured frames
         * @returns {Array}
         */
        getFrames: function() {
            return this.state.capturedFrames.slice();
        },
        
        /**
         * Get frame count
         * @returns {number}
         */
        getFrameCount: function() {
            return this.state.capturedFrames.length;
        },
        
        /**
         * Get base64 encodings
         * @returns {Array}
         */
        getEncodings: function() {
            return this.state.capturedFrames
                .map(function(frame) { return frame.encoding; })
                .filter(function(enc) { return !!enc; });
        },
        
        /**
         * Get next suggested angle/pose
         * @returns {Object|null}
         */
        getNextAngle: function() {
            if (!this.config.enableDiversity) {
                return null;
            }
            
            var prompts = {
                center: { label: 'Look straight ahead', icon: 'fa-circle-dot' },
                left: { label: 'Turn head slightly LEFT', icon: 'fa-arrow-left' },
                right: { label: 'Turn head slightly RIGHT', icon: 'fa-arrow-right' },
                up: { label: 'Tilt head slightly UP', icon: 'fa-arrow-up' },
                down: { label: 'Tilt head slightly DOWN', icon: 'fa-arrow-down' }
            };
            
            for (var i = 0; i < this.config.requiredBuckets.length; i++) {
                var bucket = this.config.requiredBuckets[i];
                if (!this.state.capturedBuckets[bucket]) {
                    return {
                        bucket: bucket,
                        label: prompts[bucket].label,
                        icon: prompts[bucket].icon
                    };
                }
            }
            
            return null;
        },
        
        /**
         * Capture a single frame
         * @private
         */
        _captureFrame: function() {
            var self = this;
            
            if (!this.state.camera || !this.state.camera.isActive()) {
                return;
            }
            
            // Capture from camera
            var canvas = this.state.camera.capture(
                this.config.captureWidth,
                this.config.captureHeight
            );
            
            if (!canvas) {
                return;
            }
            
            // Convert to blob
            this._canvasToBlob(canvas, 'image/jpeg', this.config.jpegQuality, function(blob) {
                if (!blob) {
                    return;
                }
                
                // Send to server
                self._processFrame(blob);
            });
        },
        
        /**
         * Process captured frame
         * @private
         */
        _processFrame: function(blob) {
            var self = this;
            
            // Use API to scan frame
            if (!window.FaceAttend || !window.FaceAttend.API) {
                this._triggerError('API not available');
                return;
            }
            
            window.FaceAttend.API.scanFrame(blob, function(result) {
                self._handleScanResult(result, blob);
            }, function(error) {
                self._triggerError(error.message || 'Scan failed');
            });
        },
        
        /**
         * Handle scan result
         * @private
         */
        _handleScanResult: function(result, blob) {
            // Check for errors
            if (!result || !result.ok) {
                // Silent fail - just don't capture this frame
                return;
            }
            
            // Check liveness
            if (!result.livenessOk || result.liveness < this.config.livenessThreshold) {
                // Frame rejected - liveness check failed
                return;
            }
            
            // Check for encoding
            if (!result.encoding) {
                return;
            }
            
            // Get pose bucket
            var bucket = result.poseBucket || 'center';
            this.state.currentBucket = bucket;
            
            // Check diversity if enabled
            if (this.config.enableDiversity) {
                var bucketCount = Object.keys(this.state.capturedBuckets).length;
                
                // Skip if we already have this bucket and have all 5
                if (this.state.capturedBuckets[bucket] && bucketCount >= 5) {
                    return;
                }
            }
            
            // Store frame
            var frame = {
                blob: blob,
                encoding: result.encoding,
                liveness: result.liveness,
                poseBucket: bucket,
                sharpness: result.sharpness || 0,
                faceBox: result.faceBox || null,
                timestamp: Date.now()
            };
            
            this.state.capturedFrames.push(frame);
            this.state.capturedBuckets[bucket] = true;
            
            // Trigger frame capture callback
            if (this.state.callbacks.onFrameCapture) {
                this.state.callbacks.onFrameCapture(frame);
            }
            
            // Trigger progress callback
            var progress = this.getProgress();
            if (this.state.callbacks.onProgress) {
                this.state.callbacks.onProgress(progress);
            }
            
            // Check if complete
            this._checkComplete();
        },
        
        /**
         * Check if scanning is complete
         * @private
         */
        _checkComplete: function() {
            var frameCount = this.state.capturedFrames.length;
            var bucketCount = Object.keys(this.state.capturedBuckets).length;
            
            var isComplete = false;
            
            // Complete if we have max frames
            if (frameCount >= this.config.maxFrames) {
                isComplete = true;
            }
            // Or if we have min frames AND all buckets (if diversity enabled)
            else if (frameCount >= this.config.minFrames) {
                if (!this.config.enableDiversity) {
                    isComplete = true;
                } else if (bucketCount >= this.config.requiredBuckets.length) {
                    isComplete = true;
                }
            }
            
            if (isComplete) {
                this._triggerComplete();
            }
        },
        
        /**
         * Trigger complete callback
         * @private
         */
        _triggerComplete: function() {
            this.stop();
            
            if (this.state.callbacks.onComplete) {
                this.state.callbacks.onComplete({
                    frames: this.state.capturedFrames,
                    encodings: this.getEncodings(),
                    buckets: Object.keys(this.state.capturedBuckets),
                    count: this.state.capturedFrames.length
                });
            }
        },
        
        /**
         * Trigger error callback
         * @private
         */
        _triggerError: function(message) {
            if (this.state.callbacks.onError) {
                this.state.callbacks.onError(new Error(message));
            }
        },
        
        /**
         * Convert canvas to blob
         * @private
         */
        _canvasToBlob: function(canvas, type, quality, callback) {
            if (canvas.toBlob) {
                canvas.toBlob(callback, type, quality);
            } else {
                // Fallback for older browsers
                var dataUrl = canvas.toDataURL(type, quality);
                var byteString = atob(dataUrl.split(',')[1]);
                var mimeString = dataUrl.split(',')[0].split(':')[1].split(';')[0];
                var ab = new ArrayBuffer(byteString.length);
                var ia = new Uint8Array(ab);
                
                for (var i = 0; i < byteString.length; i++) {
                    ia[i] = byteString.charCodeAt(i);
                }
                
                var blob = new Blob([ab], { type: mimeString });
                callback(blob);
            }
        }
    };

    // Expose to global namespace
    window.FaceAttend = window.FaceAttend || {};
    window.FaceAttend.FaceScan = FaceScan;

})(window);
