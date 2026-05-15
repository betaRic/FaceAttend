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
        timeout: 300000,
        
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

        buildRequestHeaders: function(extraHeaders) {
            var headers = {};
            if (window.FaceAttend && FaceAttend.Utils &&
                typeof FaceAttend.Utils.mergeRequestHeaders === 'function') {
                headers = FaceAttend.Utils.mergeRequestHeaders(extraHeaders);
            } else if (extraHeaders) {
                Object.keys(extraHeaders).forEach(function(key) {
                    headers[key] = extraHeaders[key];
                });
            }
            return headers;
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
                headers: this.buildRequestHeaders({
                    'X-Requested-With': 'XMLHttpRequest'
                })
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
                headers: this.buildRequestHeaders({
                    'X-Requested-With': 'XMLHttpRequest'
                })
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
            
            this.post('api/scan/frame', data, onSuccess, onError);
        },
        
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
            
            this.post('api/enrollment/enroll', formData, onSuccess, onError);
        }
        
        /**
         * Attendance scans intentionally use the MVC Kiosk/Attendance actions.
         * Do not add burst or streaming frame upload helpers here.
         */
    };

    // Auto-initialize with defaults
    API.init();
    
    // Expose to global namespace
    window.FaceAttend = window.FaceAttend || {};
    window.FaceAttend.API = API;

})(window);
