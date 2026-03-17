/**
 * FaceAttend Core - API Client
 * Unified API communication with consistent error handling
 * 
 * @version 1.0.0
 * @requires ES5+
 */
(function(window) {
    'use strict';

    /**
     * API client for server communication
     */
    var API = {
        // Base URL for API calls
        baseUrl: '',
        
        // CSRF token for POST requests
        csrfToken: '',
        
        // Default timeout in milliseconds
        timeout: 30000,
        
        /**
         * Initialize API client
         * 
         * @param {Object} options - Configuration options
         * @param {string} options.baseUrl - Base URL for API
         * @param {string} options.csrfToken - CSRF token
         * @param {number} options.timeout - Request timeout
         */
        init: function(options) {
            options = options || {};
            
            this.baseUrl = options.baseUrl || 
                (document.body && document.body.getAttribute('data-app-base')) || '/';
            
            // Ensure trailing slash
            if (this.baseUrl && !this.baseUrl.endsWith('/')) {
                this.baseUrl += '/';
            }
            
            this.csrfToken = options.csrfToken || 
                this.getCsrfTokenFromPage() || '';
            
            this.timeout = options.timeout || this.timeout;
        },
        
        /**
         * Get CSRF token from page
         * @returns {string|null}
         */
        getCsrfTokenFromPage: function() {
            var input = document.querySelector('input[name="__RequestVerificationToken"]');
            return input ? input.value : null;
        },
        
        /**
         * Make a POST request with FormData
         * 
         * @param {string} endpoint - API endpoint
         * @param {Object|FormData} data - Data to send
         * @param {function} onSuccess - Success callback(data)
         * @param {function} onError - Error callback(error)
         */
        post: function(endpoint, data, onSuccess, onError) {
            var self = this;
            
            onSuccess = onSuccess || function() {};
            onError = onError || function() {};
            
            // Build FormData
            var formData = new FormData();
            
            // Add CSRF token
            formData.append('__RequestVerificationToken', this.csrfToken);
            
            // Add data
            if (data) {
                if (data instanceof FormData) {
                    // Copy from existing FormData
                    data.forEach(function(value, key) {
                        formData.append(key, value);
                    });
                } else {
                    // Add from object
                    Object.keys(data).forEach(function(key) {
                        var value = data[key];
                        if (value !== null && value !== undefined) {
                            if (Array.isArray(value)) {
                                value.forEach(function(item) {
                                    formData.append(key, item);
                                });
                            } else {
                                formData.append(key, value);
                            }
                        }
                    });
                }
            }
            
            // Build URL
            var url = this.baseUrl + endpoint;
            
            // Create AbortController for timeout
            var controller = new AbortController();
            var timeoutId = setTimeout(function() {
                controller.abort();
            }, this.timeout);
            
            // Make request
            fetch(url, {
                method: 'POST',
                body: formData,
                credentials: 'same-origin',
                signal: controller.signal,
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            })
            .then(function(response) {
                clearTimeout(timeoutId);
                
                if (!response.ok) {
                    if (response.status === 429) {
                        throw new Error('Rate limit exceeded. Please wait a moment.');
                    }
                    throw new Error('Server error: ' + response.status);
                }
                
                return response.json();
            })
            .then(function(data) {
                onSuccess(data);
            })
            .catch(function(error) {
                clearTimeout(timeoutId);
                
                if (error.name === 'AbortError') {
                    onError(new Error('Request timed out. Please try again.'));
                } else {
                    onError(error);
                }
            });
        },
        
        /**
         * Make a GET request
         * 
         * @param {string} endpoint - API endpoint
         * @param {function} onSuccess - Success callback(data)
         * @param {function} onError - Error callback(error)
         */
        get: function(endpoint, onSuccess, onError) {
            var self = this;
            
            onSuccess = onSuccess || function() {};
            onError = onError || function() {};
            
            var url = this.baseUrl + endpoint;
            
            var controller = new AbortController();
            var timeoutId = setTimeout(function() {
                controller.abort();
            }, this.timeout);
            
            fetch(url, {
                method: 'GET',
                credentials: 'same-origin',
                signal: controller.signal,
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            })
            .then(function(response) {
                clearTimeout(timeoutId);
                
                if (!response.ok) {
                    throw new Error('Server error: ' + response.status);
                }
                
                return response.json();
            })
            .then(function(data) {
                onSuccess(data);
            })
            .catch(function(error) {
                clearTimeout(timeoutId);
                
                if (error.name === 'AbortError') {
                    onError(new Error('Request timed out'));
                } else {
                    onError(error);
                }
            });
        },
        
        /**
         * Scan frame for face detection
         * 
         * @param {Blob} imageBlob - Image data
         * @param {Object} options - Additional options
         * @param {number} options.faceX - Face bounding box X
         * @param {number} options.faceY - Face bounding box Y
         * @param {number} options.faceW - Face bounding box width
         * @param {number} options.faceH - Face bounding box height
         * @param {function} onSuccess - Success callback
         * @param {function} onError - Error callback
         */
        scanFrame: function(imageBlob, options, onSuccess, onError) {
            if (typeof options === 'function') {
                onError = onSuccess;
                onSuccess = options;
                options = {};
            }
            
            var data = { image: imageBlob };
            
            if (options) {
                if (options.faceX !== undefined) data.faceX = options.faceX;
                if (options.faceY !== undefined) data.faceY = options.faceY;
                if (options.faceW !== undefined) data.faceW = options.faceW;
                if (options.faceH !== undefined) data.faceH = options.faceH;
            }
            
            this.post('api/scan/frame', data, onSuccess, onError);
        },
        
        /**
         * Enroll face(s) for employee
         * 
         * @param {string} employeeId - Employee ID
         * @param {Blob[]} imageBlobs - Array of image blobs
         * @param {Object} options - Additional options
         * @param {string[]} options.allEncodings - Base64 encodings of all frames
         * @param {function} onSuccess - Success callback
         * @param {function} onError - Error callback
         */
        enroll: function(employeeId, imageBlobs, options, onSuccess, onError) {
            if (typeof options === 'function') {
                onError = onSuccess;
                onSuccess = options;
                options = {};
            }
            
            var formData = new FormData();
            formData.append('employeeId', employeeId);
            
            imageBlobs.forEach(function(blob, index) {
                formData.append('images', blob, 'frame_' + index + '.jpg');
            });
            
            if (options && options.allEncodings) {
                formData.append('allEncodingsJson', JSON.stringify(options.allEncodings));
            }
            
            this.post('api/enrollment/enroll', formData, onSuccess, onError);
        },
        
        /**
         * Record attendance
         * 
         * @param {Blob} imageBlob - Image data
         * @param {Object} options - Additional options
         * @param {number} options.lat - GPS latitude
         * @param {number} options.lon - GPS longitude
         * @param {number} options.accuracy - GPS accuracy
         * @param {string} options.deviceToken - Device token
         * @param {function} onSuccess - Success callback
         * @param {function} onError - Error callback
         */
        attend: function(imageBlob, options, onSuccess, onError) {
            if (typeof options === 'function') {
                onError = onSuccess;
                onSuccess = options;
                options = {};
            }
            
            var formData = new FormData();
            formData.append('image', imageBlob);
            
            if (options) {
                if (options.lat !== undefined) formData.append('lat', options.lat);
                if (options.lon !== undefined) formData.append('lon', options.lon);
                if (options.accuracy !== undefined) formData.append('accuracy', options.accuracy);
                if (options.deviceToken) formData.append('deviceToken', options.deviceToken);
            }
            
            this.post('api/attendance/record', formData, onSuccess, onError);
        },
        
        /**
         * Record attendance with burst mode (multiple frames)
         * 
         * @param {Blob[]} imageBlobs - Array of image blobs
         * @param {Object} options - Additional options
         * @param {function} onSuccess - Success callback
         * @param {function} onError - Error callback
         */
        attendBurst: function(imageBlobs, options, onSuccess, onError) {
            if (typeof options === 'function') {
                onError = onSuccess;
                onSuccess = options;
                options = {};
            }
            
            var formData = new FormData();
            
            imageBlobs.forEach(function(blob, index) {
                formData.append('frames', blob, 'frame_' + index + '.jpg');
            });
            
            if (options) {
                if (options.lat !== undefined) formData.append('lat', options.lat);
                if (options.lon !== undefined) formData.append('lon', options.lon);
                if (options.accuracy !== undefined) formData.append('accuracy', options.accuracy);
            }
            
            this.post('api/attendance/burst', formData, onSuccess, onError);
        },
        
        /**
         * Check device registration status
         * 
         * @param {function} onSuccess - Success callback
         * @param {function} onError - Error callback
         */
        checkDeviceStatus: function(onSuccess, onError) {
            this.get('api/device/status', onSuccess, onError);
        },
        
        /**
         * Register device
         * 
         * @param {string} employeeId - Employee ID
         * @param {string} deviceName - Device name
         * @param {function} onSuccess - Success callback
         * @param {function} onError - Error callback
         */
        registerDevice: function(employeeId, deviceName, onSuccess, onError) {
            this.post('api/device/register', {
                employeeId: employeeId,
                deviceName: deviceName
            }, onSuccess, onError);
        }
    };

    // Auto-initialize with defaults
    API.init();
    
    // Expose to global namespace
    window.FaceAttend = window.FaceAttend || {};
    window.FaceAttend.API = API;

})(window);
