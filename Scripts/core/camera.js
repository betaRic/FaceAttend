/**
 * FaceAttend Core - Camera Manager
 * Unified camera handling for all flows
 * 
 * @version 1.0.0
 * @requires ES5+
 */
(function(window) {
    'use strict';

    /**
     * Camera state and configuration
     */
    var Camera = {
        // Current stream reference
        stream: null,
        
        // Video element reference
        video: null,
        
        // Configuration
        config: {
            facingMode: 'user',
            width: { ideal: 1280 },
            height: { ideal: 720 },
            frameRate: { ideal: 30 }
        },
        
        /**
         * Check if getUserMedia is supported
         * @returns {boolean}
         */
        isSupported: function() {
            return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
        },
        
        /**
         * Initialize and start camera
         * 
         * @param {HTMLVideoElement} videoElement - Video element to attach stream to
         * @param {Object} options - Camera options
         * @param {string} options.facingMode - 'user' or 'environment'
         * @param {Object} options.width - Width constraints
         * @param {Object} options.height - Height constraints
         * @param {function} onSuccess - Success callback
         * @param {function} onError - Error callback
         */
        start: function(videoElement, options, onSuccess, onError) {
            var self = this;
            
            // Handle optional options
            if (typeof options === 'function') {
                onError = onSuccess;
                onSuccess = options;
                options = {};
            }
            
            options = options || {};
            onSuccess = onSuccess || function() {};
            onError = onError || function() {};
            
            // Check support
            if (!this.isSupported()) {
                onError(new Error('Camera API not supported in this browser'));
                return;
            }
            
            // Stop any existing stream
            this.stop();
            
            // Build constraints
            var constraints = {
                video: {
                    facingMode: options.facingMode || this.config.facingMode,
                    width: options.width || this.config.width,
                    height: options.height || this.config.height,
                    frameRate: options.frameRate || this.config.frameRate
                },
                audio: false
            };
            
            // Store video reference
            this.video = videoElement;
            
            // Request camera access
            navigator.mediaDevices.getUserMedia(constraints)
                .then(function(stream) {
                    self.stream = stream;
                    
                    if (self.video) {
                        self.video.srcObject = stream;
                        
                        // Handle metadata loaded
                        self.video.onloadedmetadata = function() {
                            self.video.play()
                                .then(function() {
                                    onSuccess(stream);
                                })
                                .catch(function(err) {
                                    onError(err);
                                });
                        };
                        
                        // Handle errors
                        self.video.onerror = function(err) {
                            onError(new Error('Video playback error'));
                        };
                    } else {
                        onSuccess(stream);
                    }
                })
                .catch(function(err) {
                    var errorMessage = 'Camera access denied';
                    
                    if (err.name === 'NotAllowedError') {
                        errorMessage = 'Camera permission denied. Please allow camera access.';
                    } else if (err.name === 'NotFoundError') {
                        errorMessage = 'No camera found on this device.';
                    } else if (err.name === 'NotReadableError') {
                        errorMessage = 'Camera is already in use by another application.';
                    }
                    
                    onError(new Error(errorMessage));
                });
        },
        
        /**
         * Stop camera stream
         */
        stop: function() {
            if (this.stream) {
                this.stream.getTracks().forEach(function(track) {
                    track.stop();
                });
                this.stream = null;
            }
            
            if (this.video) {
                this.video.srcObject = null;
                this.video = null;
            }
        },
        
        /**
         * Capture current frame as canvas
         * 
         * @param {number} targetWidth - Desired width (preserves aspect ratio)
         * @param {number} targetHeight - Desired height (optional)
         * @returns {HTMLCanvasElement|null}
         */
        capture: function(targetWidth, targetHeight) {
            if (!this.video || !this.video.videoWidth) {
                return null;
            }
            
            var canvas = document.createElement('canvas');
            var ctx = canvas.getContext('2d');
            
            // Determine dimensions
            var width = targetWidth || this.video.videoWidth;
            var height = targetHeight;
            
            if (!height) {
                // Preserve aspect ratio
                var aspectRatio = this.video.videoHeight / this.video.videoWidth;
                height = Math.round(width * aspectRatio);
            }
            
            canvas.width = width;
            canvas.height = height;
            
            // Draw frame
            ctx.drawImage(this.video, 0, 0, width, height);
            
            return canvas;
        },
        
        /**
         * Capture frame and return as blob
         * 
         * @param {string} type - MIME type (default: 'image/jpeg')
         * @param {number} quality - Quality 0-1 (default: 0.85)
         * @param {function} callback - Callback(blob)
         */
        captureAsBlob: function(type, quality, callback) {
            if (typeof type === 'function') {
                callback = type;
                type = 'image/jpeg';
                quality = 0.85;
            }
            
            type = type || 'image/jpeg';
            quality = quality || 0.85;
            
            var canvas = this.capture();
            if (!canvas) {
                callback(null);
                return;
            }
            
            canvas.toBlob(function(blob) {
                callback(blob);
            }, type, quality);
        },
        
        /**
         * Check if camera is currently active
         * @returns {boolean}
         */
        isActive: function() {
            return !!(this.stream && this.stream.active);
        },
        
        /**
         * Get current video dimensions
         * @returns {Object|null}
         */
        getDimensions: function() {
            if (!this.video) {
                return null;
            }
            
            return {
                width: this.video.videoWidth,
                height: this.video.videoHeight
            };
        },
        
        /**
         * Pause video playback
         */
        pause: function() {
            if (this.video) {
                this.video.pause();
            }
        },
        
        /**
         * Resume video playback
         */
        resume: function() {
            if (this.video) {
                this.video.play();
            }
        }
    };

    // Expose to global namespace
    window.FaceAttend = window.FaceAttend || {};
    window.FaceAttend.Camera = Camera;

})(window);
