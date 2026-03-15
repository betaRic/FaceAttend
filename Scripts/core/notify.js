/**
 * FaceAttend Core - Notification Manager
 * Unified toast and modal notifications
 * 
 * @version 1.0.0
 * @requires ES5+
 * @optional SweetAlert2
 * @optional Toastify
 */
(function(window) {
    'use strict';

    /**
     * Notification manager
     */
    var Notify = {
        // Default duration in milliseconds
        defaultDuration: 4000,
        
        // Success duration (shorter)
        successDuration: 3000,
        
        // Error duration (longer)
        errorDuration: 5000,
        
        // Current toast element
        currentToast: null,
        
        /**
         * Show a toast notification
         * 
         * @param {string} message - Message to display
         * @param {Object} options - Options
         * @param {string} options.type - 'success', 'error', 'warning', 'info'
         * @param {number} options.duration - Duration in milliseconds
         * @param {string} options.position - Position on screen
         */
        toast: function(message, options) {
            options = options || {};
            
            var type = options.type || 'info';
            var duration = options.duration || 
                (type === 'success' ? this.successDuration : 
                 type === 'error' ? this.errorDuration : this.defaultDuration);
            var position = options.position || 'top-end';
            
            // Try SweetAlert2 first (best experience)
            if (window.Swal) {
                this._swalToast(message, type, duration, position);
            }
            // Fallback to Toastify
            else if (window.Toastify) {
                this._toastifyToast(message, type, duration);
            }
            // Final fallback: console + simple alert for errors
            else {
                this._fallbackToast(message, type);
            }
        },
        
        /**
         * SweetAlert2 toast implementation
         * @private
         */
        _swalToast: function(message, type, duration, position) {
            var colors = {
                success: { bg: '#f0fdf4', text: '#166534', icon: 'success' },
                error: { bg: '#fef2f2', text: '#991b1b', icon: 'error' },
                warning: { bg: '#fffbeb', text: '#92400e', icon: 'warning' },
                info: { bg: '#eff6ff', text: '#1e40af', icon: 'info' }
            };
            
            var color = colors[type] || colors.info;
            
            window.Swal.fire({
                title: message,
                icon: color.icon,
                toast: true,
                position: position,
                showConfirmButton: false,
                timer: duration,
                timerProgressBar: true,
                background: color.bg,
                color: color.text,
                customClass: {
                    popup: 'facescan-toast'
                },
                didOpen: function(popup) {
                    popup.style.borderRadius = '12px';
                    popup.style.boxShadow = '0 10px 40px rgba(0,0,0,0.2)';
                }
            });
        },
        
        /**
         * Toastify implementation
         * @private
         */
        _toastifyToast: function(message, type, duration) {
            var colors = {
                success: '#1a6b3a',
                error: '#6b1a1a',
                warning: '#6b4a1a',
                info: '#1a3a6b'
            };
            
            window.Toastify({
                text: message,
                duration: duration,
                gravity: 'bottom',
                position: 'right',
                close: true,
                stopOnFocus: true,
                style: { 
                    background: colors[type] || colors.info,
                    borderRadius: '8px',
                    boxShadow: '0 4px 12px rgba(0,0,0,0.3)'
                }
            }).showToast();
        },
        
        /**
         * Fallback toast implementation
         * @private
         */
        _fallbackToast: function(message, type) {
            console.log('[' + type.toUpperCase() + ']', message);
            
            // For errors, also show alert
            if (type === 'error') {
                window.alert(message);
            }
        },
        
        /**
         * Show success toast
         * @param {string} message
         * @param {Object} options
         */
        success: function(message, options) {
            options = options || {};
            options.type = 'success';
            this.toast(message, options);
        },
        
        /**
         * Show error toast
         * @param {string} message
         * @param {Object} options
         */
        error: function(message, options) {
            options = options || {};
            options.type = 'error';
            this.toast(message, options);
        },
        
        /**
         * Show warning toast
         * @param {string} message
         * @param {Object} options
         */
        warning: function(message, options) {
            options = options || {};
            options.type = 'warning';
            this.toast(message, options);
        },
        
        /**
         * Show info toast
         * @param {string} message
         * @param {Object} options
         */
        info: function(message, options) {
            options = options || {};
            options.type = 'info';
            this.toast(message, options);
        },
        
        /**
         * Show confirmation modal
         * 
         * @param {string} title - Modal title
         * @param {string} text - Modal text
         * @param {Object} options - Options
         * @param {string} options.confirmText - Confirm button text
         * @param {string} options.cancelText - Cancel button text
         * @param {string} options.icon - Icon type
         * @param {function} callback - Callback(confirmed)
         */
        confirm: function(title, text, options, callback) {
            if (typeof options === 'function') {
                callback = options;
                options = {};
            }
            
            options = options || {};
            callback = callback || function() {};
            
            var confirmText = options.confirmText || 'OK';
            var cancelText = options.cancelText || 'Cancel';
            var icon = options.icon || 'question';
            
            // Use SweetAlert2 if available
            if (window.Swal) {
                window.Swal.fire({
                    title: title,
                    text: text,
                    icon: icon,
                    showCancelButton: true,
                    confirmButtonText: confirmText,
                    cancelButtonText: cancelText,
                    confirmButtonColor: '#3b82f6',
                    cancelButtonColor: '#64748b',
                    background: '#0f172a',
                    color: '#f8fafc',
                    customClass: {
                        popup: 'facescan-modal'
                    }
                }).then(function(result) {
                    callback(result.isConfirmed);
                });
            }
            // Fallback to native confirm
            else {
                var confirmed = window.confirm(title + '\n\n' + text);
                callback(confirmed);
            }
        },
        
        /**
         * Show loading state
         * 
         * @param {string} title - Loading title
         * @param {string} text - Loading text
         * @returns {function} Function to close the loading modal
         */
        loading: function(title, text) {
            title = title || 'Processing...';
            text = text || '';
            
            if (window.Swal) {
                window.Swal.fire({
                    title: title,
                    text: text,
                    allowOutsideClick: false,
                    allowEscapeKey: false,
                    showConfirmButton: false,
                    background: '#0f172a',
                    color: '#f8fafc',
                    didOpen: function() {
                        window.Swal.showLoading();
                    }
                });
                
                return function() {
                    window.Swal.close();
                };
            }
            // Fallback: return no-op
            return function() {};
        },
        
        /**
         * Close any open modal/toast
         */
        close: function() {
            if (window.Swal) {
                window.Swal.close();
            }
        },
        
        /**
         * Show success modal (not toast)
         * 
         * @param {string} title
         * @param {string} text
         * @param {function} callback
         */
        successModal: function(title, text, callback) {
            callback = callback || function() {};
            
            if (window.Swal) {
                window.Swal.fire({
                    title: title,
                    text: text,
                    icon: 'success',
                    confirmButtonText: 'Continue',
                    confirmButtonColor: '#10b981',
                    background: '#0f172a',
                    color: '#f8fafc'
                }).then(function() {
                    callback();
                });
            } else {
                window.alert(title + '\n' + text);
                callback();
            }
        },
        
        /**
         * Show error modal (not toast)
         * 
         * @param {string} title
         * @param {string} text
         * @param {function} callback
         */
        errorModal: function(title, text, callback) {
            callback = callback || function() {};
            
            if (window.Swal) {
                window.Swal.fire({
                    title: title,
                    text: text,
                    icon: 'error',
                    confirmButtonText: 'OK',
                    confirmButtonColor: '#ef4444',
                    background: '#0f172a',
                    color: '#f8fafc'
                }).then(function() {
                    callback();
                });
            } else {
                window.alert('Error: ' + title + '\n' + text);
                callback();
            }
        },
        
        /**
         * Prompt for input
         * 
         * @param {string} title
         * @param {string} text
         * @param {Object} options
         * @param {function} callback - Callback(value)
         */
        prompt: function(title, text, options, callback) {
            if (typeof options === 'function') {
                callback = options;
                options = {};
            }
            
            options = options || {};
            callback = callback || function() {};
            
            if (window.Swal) {
                window.Swal.fire({
                    title: title,
                    text: text,
                    input: options.inputType || 'text',
                    inputPlaceholder: options.placeholder || '',
                    inputValue: options.defaultValue || '',
                    showCancelButton: true,
                    confirmButtonText: options.confirmText || 'OK',
                    cancelButtonText: options.cancelText || 'Cancel',
                    confirmButtonColor: '#3b82f6',
                    cancelButtonColor: '#64748b',
                    background: '#0f172a',
                    color: '#f8fafc',
                    preConfirm: function(value) {
                        if (options.required && !value) {
                            window.Swal.showValidationMessage('This field is required');
                        }
                        return value;
                    }
                }).then(function(result) {
                    if (result.isConfirmed) {
                        callback(result.value);
                    } else {
                        callback(null);
                    }
                });
            } else {
                var value = window.prompt(title + '\n' + text, options.defaultValue || '');
                callback(value);
            }
        }
    };

    // ============================================================================
    // Theme-aware helpers
    // ============================================================================
    
    /**
     * Get theme colors based on current theme
     * @private
     */
    Notify._getTheme = function() {
        var theme = document.documentElement.getAttribute('data-theme');
        var isDark = theme === 'kiosk' || theme === 'dark' || document.body.classList.contains('mobile-app');
        
        if (isDark) {
            return {
                bg: '#0f172a',
                color: '#f8fafc',
                confirmColor: '#3b82f6',
                cancelColor: '#64748b'
            };
        }
        return {
            bg: '#ffffff',
            color: '#0f172a',
            confirmColor: '#3b82f6',
            cancelColor: '#6b7280'
        };
    };
    
    /**
     * Theme-aware confirm dialog (replaces admin.js confirmDialog)
     * @param {Object} options - Options object
     * @param {string} options.title - Dialog title
     * @param {string} options.text - Dialog text
     * @param {string} options.icon - Icon type ('warning', 'info', 'question', etc.)
     * @param {string} options.confirmText - Confirm button text
     * @param {string} options.cancelText - Cancel button text
     * @returns {Promise<boolean>} - Resolves to true if confirmed
     */
    Notify.confirmDialog = function(options) {
        var theme = this._getTheme();
        
        if (window.Swal) {
            return window.Swal.fire({
                title: options.title || 'Confirm',
                text: options.text || '',
                icon: options.icon || 'question',
                showCancelButton: true,
                confirmButtonText: options.confirmText || 'Continue',
                cancelButtonText: options.cancelText || 'Cancel',
                confirmButtonColor: theme.confirmColor,
                cancelButtonColor: theme.cancelColor,
                background: theme.bg,
                color: theme.color,
                customClass: {
                    popup: 'facescan-modal'
                }
            }).then(function(result) {
                return !!(result && result.isConfirmed);
            });
        }
        
        // Fallback to native confirm
        var message = options.text ? options.title + '\n\n' + options.text : options.title;
        return Promise.resolve(window.confirm(message));
    };
    
    /**
     * Theme-aware toast with automatic theme detection
     * @param {string} message - Toast message
     * @param {string} type - Toast type ('success', 'error', 'warning', 'info')
     * @param {number} duration - Duration in milliseconds
     */
    Notify.toastTheme = function(message, type, duration) {
        var theme = this._getTheme();
        type = type || 'info';
        duration = duration || (type === 'success' ? 3000 : type === 'error' ? 5000 : 4000);
        
        // Theme-aware colors
        var colors = {
            success: { bg: theme.isDark ? '#064e3b' : '#f0fdf4', text: theme.isDark ? '#6ee7b7' : '#166534' },
            error: { bg: theme.isDark ? '#7f1d1d' : '#fef2f2', text: theme.isDark ? '#fca5a5' : '#991b1b' },
            warning: { bg: theme.isDark ? '#78350f' : '#fffbeb', text: theme.isDark ? '#fcd34d' : '#92400e' },
            info: { bg: theme.isDark ? '#1e3a8a' : '#eff6ff', text: theme.isDark ? '#93c5fd' : '#1e40af' }
        };
        
        var color = colors[type] || colors.info;
        
        if (window.Swal) {
            window.Swal.fire({
                title: message,
                icon: type,
                toast: true,
                position: 'top-end',
                showConfirmButton: false,
                timer: duration,
                timerProgressBar: true,
                background: color.bg,
                color: color.text,
                customClass: {
                    popup: 'facescan-toast'
                }
            });
        } else if (window.Toastify) {
            window.Toastify({
                text: message,
                duration: duration,
                gravity: 'bottom',
                position: 'right',
                style: {
                    background: color.bg,
                    color: color.text,
                    borderRadius: '8px'
                }
            }).showToast();
        } else {
            console.log('[' + type.toUpperCase() + ']', message);
        }
    };

    // ============================================================================
    // Backward compatibility: window.ui bindings (used by admin.js)
    // ============================================================================
    window.ui = window.ui || {};
    window.ui.toast = Notify.toast.bind(Notify);
    window.ui.toastSuccess = Notify.success.bind(Notify);
    window.ui.toastError = Notify.error.bind(Notify);
    window.ui.toastWarning = Notify.warning.bind(Notify);
    window.ui.toastInfo = Notify.info.bind(Notify);
    window.ui.confirm = Notify.confirmDialog.bind(Notify);
    window.ui.confirmDialog = Notify.confirmDialog.bind(Notify);
    window.ui.loading = Notify.loading.bind(Notify);
    window.ui.close = Notify.close.bind(Notify);
    window.ui.successModal = Notify.successModal.bind(Notify);
    window.ui.errorModal = Notify.errorModal.bind(Notify);
    window.ui.prompt = Notify.prompt.bind(Notify);

    // ============================================================================
    // Expose to FaceAttend namespace
    // ============================================================================
    window.FaceAttend = window.FaceAttend || {};
    window.FaceAttend.Notify = Notify;

})(window);
