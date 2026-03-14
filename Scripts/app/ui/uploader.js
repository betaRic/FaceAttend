/**
 * FaceAttend UI - Uploader Component
 * Drag-and-drop file upload with validation
 * 
 * @version 2.0
 * @requires FaceAttend core
 */

var FaceAttend = window.FaceAttend || {};
FaceAttend.UI = FaceAttend.UI || {};

/**
 * Uploader Component
 * File upload with drag-drop, validation, and preview
 */
FaceAttend.UI.Uploader = {
    /**
     * Initialize uploader
     * @param {string} containerId - Uploader container element ID
     * @param {Object} options - Configuration options
     * @returns {Object} Uploader instance
     */
    init: function(containerId, options) {
        var instance = {
            container: document.getElementById(containerId),
            options: Object.assign({
                maxFiles: 5,
                maxSize: 10 * 1024 * 1024, // 10MB
                allowedTypes: ['image/jpeg', 'image/png', 'image/jpg'],
                allowedExtensions: ['.jpg', '.jpeg', '.png'],
                onFilesSelected: null,
                onError: null,
                onProgress: null
            }, options),
            files: [],
            isLoading: false
        };

        if (!instance.container) {
            console.error('[FaceAttend.UI.Uploader] Container not found:', containerId);
            return null;
        }

        // Read data attributes from DOM
        var maxFilesAttr = instance.container.dataset.maxFiles;
        var maxSizeAttr = instance.container.dataset.maxSize;
        var acceptAttr = instance.container.dataset.accept;
        
        if (maxFilesAttr) instance.options.maxFiles = parseInt(maxFilesAttr, 10);
        if (maxSizeAttr) instance.options.maxSize = parseInt(maxSizeAttr, 10);
        if (acceptAttr) {
            instance.options.allowedExtensions = acceptAttr.split(',').map(function(ext) {
                return ext.trim();
            });
        }

        this._setupElements(instance);
        this._attachEvents(instance);
        
        // Store reference
        instance.container._uploaderInstance = instance;
        
        return instance;
    },

    /**
     * Get uploader instance by container ID
     * @param {string} containerId 
     * @returns {Object} Uploader instance
     */
    get: function(containerId) {
        var container = document.getElementById(containerId);
        return container ? container._uploaderInstance : null;
    },

    /**
     * Get selected files
     * @param {string} containerId - Uploader container ID
     * @returns {File[]} Array of selected files
     */
    getFiles: function(containerId) {
        var instance = this.get(containerId);
        return instance ? instance.files : [];
    },

    /**
     * Clear all selected files
     * @param {string} containerId - Uploader container ID
     */
    clear: function(containerId) {
        var instance = this.get(containerId);
        if (!instance) return;

        instance.files = [];
        
        var input = instance.container.querySelector('.fa-uploader__input');
        if (input) input.value = '';
        
        var filesContainer = instance.container.querySelector('.fa-uploader__files');
        if (filesContainer) filesContainer.innerHTML = '';
        
        this._hideError(instance);
        this._setLoading(instance, false);
    },

    /**
     * Set loading state
     * @param {string} containerId - Uploader container ID
     * @param {boolean} isLoading - Loading state
     */
    setLoading: function(containerId, isLoading) {
        var instance = this.get(containerId);
        if (!instance) return;
        this._setLoading(instance, isLoading);
    },

    /**
     * Show error message
     * @param {string} containerId - Uploader container ID
     * @param {string} message - Error message
     */
    showError: function(containerId, message) {
        var instance = this.get(containerId);
        if (!instance) return;
        this._showError(instance, message);
    },

    /**
     * Internal: Setup DOM element references
     * @private
     */
    _setupElements: function(instance) {
        instance.elements = {
            dropzone: instance.container.querySelector('.fa-uploader__dropzone'),
            input: instance.container.querySelector('.fa-uploader__input'),
            files: instance.container.querySelector('.fa-uploader__files'),
            error: instance.container.querySelector('.fa-uploader__error')
        };
    },

    /**
     * Internal: Attach event listeners
     * @private
     */
    _attachEvents: function(instance) {
        var self = this;
        var dropzone = instance.elements.dropzone;
        var input = instance.elements.input;

        if (!dropzone || !input) return;

        // Click to browse
        dropzone.addEventListener('click', function(e) {
            if (e.target !== input && !instance.isLoading) {
                input.click();
            }
        });

        // File selection
        input.addEventListener('change', function(e) {
            if (e.target.files.length > 0) {
                self._handleFiles(instance, e.target.files);
            }
        });

        // Drag and drop
        dropzone.addEventListener('dragover', function(e) {
            e.preventDefault();
            if (!instance.isLoading) {
                this.classList.add('is-dragover');
            }
        });

        dropzone.addEventListener('dragleave', function() {
            this.classList.remove('is-dragover');
        });

        dropzone.addEventListener('drop', function(e) {
            e.preventDefault();
            this.classList.remove('is-dragover');
            if (!instance.isLoading && e.dataTransfer.files.length > 0) {
                self._handleFiles(instance, e.dataTransfer.files);
            }
        });

        // Prevent default drag behavior on document
        document.addEventListener('dragover', function(e) {
            e.preventDefault();
        });
        
        document.addEventListener('drop', function(e) {
            if (!instance.container.contains(e.target)) {
                e.preventDefault();
            }
        });
    },

    /**
     * Internal: Handle file selection
     * @private
     */
    _handleFiles: function(instance, fileList) {
        var files = Array.prototype.slice.call(fileList, 0, instance.options.maxFiles);
        var validFiles = [];
        var errors = [];
        var self = this;

        files.forEach(function(file) {
            // Validate type
            var fileExt = '.' + file.name.split('.').pop().toLowerCase();
            var isValidType = instance.options.allowedExtensions.some(function(ext) {
                return ext.toLowerCase() === fileExt;
            });

            if (!isValidType) {
                errors.push(file.name + ': Invalid file type');
                return;
            }

            // Validate size
            if (file.size > instance.options.maxSize) {
                var maxSizeMB = Math.round(instance.options.maxSize / 1024 / 1024);
                errors.push(file.name + ': File too large (max ' + maxSizeMB + 'MB)');
                return;
            }

            validFiles.push(file);
        });

        instance.files = validFiles;
        this._renderFiles(instance);
        this._showErrors(instance, errors);

        // Call callback
        if (validFiles.length > 0 && instance.options.onFilesSelected) {
            instance.options.onFilesSelected(validFiles);
        }

        // Call data attribute callback if present
        var onSelectedAttr = instance.container.dataset.onSelected;
        if (onSelectedAttr && typeof window[onSelectedAttr] === 'function') {
            window[onSelectedAttr](validFiles);
        }
    },

    /**
     * Internal: Render file chips
     * @private
     */
    _renderFiles: function(instance) {
        var container = instance.elements.files;
        if (!container) return;

        container.innerHTML = '';

        instance.files.forEach(function(file, idx) {
            var chip = document.createElement('div');
            chip.className = 'fa-uploader__chip';
            chip.innerHTML = '<i class="fa-solid fa-image"></i>' +
                           '<span>' + this._truncate(file.name, 20) + '</span>' +
                           '<button type="button" class="fa-uploader__chip-remove" data-idx="' + idx + '">' +
                           '<i class="fa-solid fa-xmark"></i></button>';
            container.appendChild(chip);
        }.bind(this));

        // Attach remove handlers
        var removeButtons = container.querySelectorAll('.fa-uploader__chip-remove');
        var self = this;
        removeButtons.forEach(function(btn) {
            btn.addEventListener('click', function(e) {
                e.stopPropagation();
                var idx = parseInt(this.dataset.idx, 10);
                self._removeFile(instance, idx);
            });
        });
    },

    /**
     * Internal: Remove a file
     * @private
     */
    _removeFile: function(instance, idx) {
        instance.files.splice(idx, 1);
        this._renderFiles(instance);
        
        if (instance.files.length === 0) {
            var input = instance.elements.input;
            if (input) input.value = '';
        }
    },

    /**
     * Internal: Show errors
     * @private
     */
    _showErrors: function(instance, errors) {
        var errorContainer = instance.elements.error;
        if (!errorContainer) return;

        if (errors.length > 0) {
            errorContainer.innerHTML = errors.join('<br>');
            errorContainer.classList.add('is-visible');
            
            if (instance.options.onError) {
                instance.options.onError(errors);
            }
            
            var onErrorAttr = instance.container.dataset.onError;
            if (onErrorAttr && typeof window[onErrorAttr] === 'function') {
                window[onErrorAttr](errors);
            }
        } else {
            this._hideError(instance);
        }
    },

    /**
     * Internal: Hide error
     * @private
     */
    _hideError: function(instance) {
        var errorContainer = instance.elements.error;
        if (errorContainer) {
            errorContainer.innerHTML = '';
            errorContainer.classList.remove('is-visible');
        }
        
        var dropzone = instance.elements.dropzone;
        if (dropzone) {
            dropzone.classList.remove('is-error');
        }
    },

    /**
     * Internal: Set loading state
     * @private
     */
    _setLoading: function(instance, isLoading) {
        instance.isLoading = isLoading;
        
        if (isLoading) {
            instance.container.classList.add('is-loading');
        } else {
            instance.container.classList.remove('is-loading');
        }
    },

    /**
     * Internal: Truncate text
     * @private
     */
    _truncate: function(str, len) {
        return str.length > len ? str.substring(0, len) + '...' : str;
    }
};
