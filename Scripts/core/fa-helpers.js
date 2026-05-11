/**
 * FaceAttend Utils - Core Helper Functions
 * Single source of truth for DOM utilities, CSRF handling, and common operations
 * 
 * Provides: FaceAttend.Utils
 * Usage: FaceAttend.Utils.$(selector), FaceAttend.Utils.getCsrfToken(), etc.
 */
(function (window) {
    'use strict';

    // Ensure namespace exists
    window.FaceAttend = window.FaceAttend || {};

    /**
     * Utility functions for DOM manipulation, events, and common operations
     */
    FaceAttend.Utils = {
        /**
         * Query a single element
         * @param {string} selector - CSS selector
         * @param {Element} [context=document] - Context element
         * @returns {Element|null}
         */
        $: function (selector, context) {
            return (context || document).querySelector(selector);
        },

        /**
         * Query all matching elements
         * @param {string} selector - CSS selector
         * @param {Element} [context=document] - Context element
         * @returns {Array<Element>}
         */
        $$: function (selector, context) {
            var nl = (context || document).querySelectorAll(selector);
            return Array.prototype.slice.call(nl);
        },

        /**
         * Get element by ID (shorthand)
         * @param {string} id - Element ID
         * @returns {Element|null}
         */
        el: function (id) {
            return document.getElementById(id);
        },

        /**
         * Create a new element with attributes
         * @param {string} tag - Tag name
         * @param {Object} [attrs={}] - Attributes to set
         * @returns {Element}
         */
        create: function (tag, attrs) {
            var el = document.createElement(tag);
            if (attrs) {
                for (var key in attrs) {
                    if (attrs.hasOwnProperty(key)) {
                        el.setAttribute(key, attrs[key]);
                    }
                }
            }
            return el;
        },

        /**
         * Get CSRF token from the page
         * @returns {string}
         */
        getCsrfToken: function () {
            var input = document.querySelector('input[name="__RequestVerificationToken"]');
            return input ? input.value : '';
        },

        /**
         * Get application base URL
         * @returns {string}
         */
        getAppBase: function () {
            var base = document.body.getAttribute('data-app-base') || '/';
            return base.replace(/\/?$/, '/');
        },

        /**
         * Debounce a function
         * @param {Function} fn - Function to debounce
         * @param {number} delay - Delay in milliseconds
         * @returns {Function}
         */
        debounce: function (fn, delay) {
            var timer = null;
            return function () {
                var context = this;
                var args = arguments;
                clearTimeout(timer);
                timer = setTimeout(function () {
                    fn.apply(context, args);
                }, delay);
            };
        },

        /**
         * Throttle a function
         * @param {Function} fn - Function to throttle
         * @param {number} limit - Limit in milliseconds
         * @returns {Function}
         */
        throttle: function (fn, limit) {
            var inThrottle = false;
            return function () {
                var context = this;
                var args = arguments;
                if (!inThrottle) {
                    fn.apply(context, args);
                    inThrottle = true;
                    setTimeout(function () {
                        inThrottle = false;
                    }, limit);
                }
            };
        },

        /**
         * Add event listener
         * @param {Element} el - Element
         * @param {string} event - Event name
         * @param {Function} handler - Event handler
         */
        on: function (el, event, handler) {
            if (el && el.addEventListener) {
                el.addEventListener(event, handler, false);
            }
        },

        /**
         * Remove event listener
         * @param {Element} el - Element
         * @param {string} event - Event name
         * @param {Function} handler - Event handler
         */
        off: function (el, event, handler) {
            if (el && el.removeEventListener) {
                el.removeEventListener(event, handler, false);
            }
        },

        /**
         * Add one-time event listener
         * @param {Element} el - Element
         * @param {string} event - Event name
         * @param {Function} handler - Event handler
         */
        once: function (el, event, handler) {
            var self = this;
            var wrapped = function (e) {
                self.off(el, event, wrapped);
                handler.call(el, e);
            };
            self.on(el, event, wrapped);
        },

        /**
         * Execute callback when DOM is ready
         * @param {Function} fn - Callback function
         */
        ready: function (fn) {
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', fn);
            } else {
                fn();
            }
        },

        /**
         * Format time from Date object
         * @param {Date} date - Date object
         * @returns {string} - Formatted time (HH:MM:SS)
         */
        formatTime: function (date) {
            if (!(date instanceof Date)) date = new Date(date);
            var h = date.getHours().toString().padStart(2, '0');
            var m = date.getMinutes().toString().padStart(2, '0');
            var s = date.getSeconds().toString().padStart(2, '0');
            return h + ':' + m + ':' + s;
        },

        /**
         * Format date from Date object
         * @param {Date} date - Date object
         * @returns {string} - Formatted date (YYYY-MM-DD)
         */
        formatDate: function (date) {
            if (!(date instanceof Date)) date = new Date(date);
            var y = date.getFullYear();
            var m = (date.getMonth() + 1).toString().padStart(2, '0');
            var d = date.getDate().toString().padStart(2, '0');
            return y + '-' + m + '-' + d;
        },

        /**
         * Check if device is mobile
         * @returns {boolean}
         */
        isMobile: function () {
            var ua = navigator.userAgent.toLowerCase();
            return /iphone|android.*mobile|windows phone|iemobile|blackberry/.test(ua);
        },

        /**
         * Check if page is served over HTTPS
         * @returns {boolean}
         */
        isHttps: function () {
            return location.protocol === 'https:';
        },

        /**
         * Check if current theme is dark
         * @returns {boolean}
         */
        isDark: function () {
            var theme = document.documentElement.getAttribute('data-theme');
            return theme === 'kiosk' || theme === 'dark' || document.body.classList.contains('mobile-app');
        },

        getClientDeviceHeaders: function () {
            var screenObj = window.screen || {};
            var ua = navigator.userAgent || '';
            var isTouch = 'ontouchstart' in window || (navigator.maxTouchPoints || 0) > 0;
            var mobileUa = /iphone|android|windows phone|iemobile|blackberry|ipad|ipod/i.test(ua);

            return {
                'X-Client-Screen-Width': String(screenObj.width || 0),
                'X-Client-Screen-Height': String(screenObj.height || 0),
                'X-Client-Pixel-Ratio': String(window.devicePixelRatio || 1),
                'X-Client-Touch-Supported': isTouch ? 'true' : 'false',
                'X-Client-Mobile-UA': mobileUa ? 'true' : 'false'
            };
        },

        mergeRequestHeaders: function (headers) {
            var merged = {};
            var deviceHeaders = this.getClientDeviceHeaders();
            var key;

            for (key in deviceHeaders) {
                if (deviceHeaders.hasOwnProperty(key)) {
                    merged[key] = deviceHeaders[key];
                }
            }

            headers = headers || {};
            for (key in headers) {
                if (headers.hasOwnProperty(key)) {
                    merged[key] = headers[key];
                }
            }

            return merged;
        },

        /**
         * Fetch JSON with CSRF token
         * @param {string} url - URL to fetch
         * @param {Object} options - Fetch options
         * @returns {Promise}
         */
        fetchJson: function (url, options) {
            options = options || {};
            options.headers = this.mergeRequestHeaders(options.headers);
            options.headers['X-Requested-With'] = 'XMLHttpRequest';

            // Add CSRF token for non-GET requests
            if (options.method && options.method !== 'GET') {
                var token = this.getCsrfToken();
                if (token) {
                    options.headers['X-CSRF-Token'] = token;
                }
            }

            return fetch(url, options).then(function (response) {
                if (!response.ok) {
                    throw new Error('HTTP ' + response.status + ': ' + response.statusText);
                }
                return response.json();
            });
        },

        /**
         * Copy text to clipboard
         * @param {string} text - Text to copy
         * @returns {Promise}
         */
        copyToClipboard: function (text) {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                return navigator.clipboard.writeText(text);
            }
            // Fallback
            var textarea = document.createElement('textarea');
            textarea.value = text;
            textarea.style.position = 'fixed';
            textarea.style.opacity = '0';
            document.body.appendChild(textarea);
            textarea.select();
            document.execCommand('copy');
            document.body.removeChild(textarea);
            return Promise.resolve();
        },

        /**
         * Scroll element into view smoothly
         * @param {Element} el - Element to scroll to
         * @param {string} [behavior='smooth'] - Scroll behavior
         */
        scrollTo: function (el, behavior) {
            if (typeof el === 'string') {
                el = document.querySelector(el);
            }
            if (el && el.scrollIntoView) {
                el.scrollIntoView({ behavior: behavior || 'smooth', block: 'nearest' });
            }
        }
    };

})(window);
