class FileSystemBrowser {
    constructor() {
        this.currentPath = '/';
        this.selectedItems = new Set();
        this.isSearchMode = false;

        this.initializeElements();
        this.bindEvents();
        this.loadFromURL();
        this.loadDirectory();
    }

    initializeElements() {
        this.elements = {
            searchInput: document.getElementById('searchInput'),
            searchBtn: document.getElementById('searchBtn'),
            clearSearchBtn: document.getElementById('clearSearchBtn'),
            fileInput: document.getElementById('fileInput'),
            uploadBtn: document.getElementById('uploadBtn'),
            refreshBtn: document.getElementById('refreshBtn'),
            uploadArea: document.getElementById('uploadArea'),
            breadcrumb: document.getElementById('breadcrumb'),
            stats: document.getElementById('stats'),
            fileList: document.getElementById('fileList'),
            loading: document.getElementById('loading'),
            error: document.getElementById('error')
        };
    }

    bindEvents() {
        // Search functionality
        this.elements.searchInput.addEventListener('keypress', (e) => {
            if (e.key === 'Enter') {
                this.performSearch();
            }
        });

        this.elements.searchBtn.addEventListener('click', () => this.performSearch());
        this.elements.clearSearchBtn.addEventListener('click', () => this.clearSearch());

        // Upload functionality
        this.elements.uploadBtn.addEventListener('click', () => this.elements.fileInput.click());
        this.elements.fileInput.addEventListener('change', (e) => this.handleFileUpload(e.target.files));

        // Drag and drop
        this.elements.uploadArea.addEventListener('dragover', this.handleDragOver.bind(this));
        this.elements.uploadArea.addEventListener('dragleave', this.handleDragLeave.bind(this));
        this.elements.uploadArea.addEventListener('drop', this.handleDrop.bind(this));

        // Refresh
        this.elements.refreshBtn.addEventListener('click', () => this.refreshCurrentView());

        // Browser navigation
        window.addEventListener('popstate', () => this.loadFromURL());
    }

    loadFromURL() {
        const urlParams = new URLSearchParams(window.location.search);
        const path = urlParams.get('path') || '/';
        const search = urlParams.get('search');

        this.currentPath = path;

        if (search) {
            this.elements.searchInput.value = search;
            this.performSearch();
        } else {
            this.loadDirectory();
        }
    }

    updateURL(path, search = null) {
        const url = new URL(window.location);
        url.searchParams.set('path', path);

        if (search) {
            url.searchParams.set('search', search);
        } else {
            url.searchParams.delete('search');
        }

        window.history.pushState({}, '', url);
    }

    showLoading() {
        this.elements.loading.classList.remove('hidden');
        this.elements.error.classList.add('hidden');
        this.elements.fileList.style.display = 'none';
    }

    hideLoading() {
        this.elements.loading.classList.add('hidden');
        this.elements.fileList.style.display = 'block';
    }

    showError(message) {
        this.elements.error.textContent = message;
        this.elements.error.classList.remove('hidden');
        this.elements.loading.classList.add('hidden');
    }

    hideError() {
        this.elements.error.classList.add('hidden');
    }

    async loadDirectory(path = this.currentPath) {
        this.showLoading();
        this.hideError();
        this.isSearchMode = false;

        try {
            const response = await fetch(`/api/filesystem/browse?path=${encodeURIComponent(path)}`);

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }

            const data = await response.json();
            this.currentPath = data.current_path;

            this.renderBreadcrumb(data.current_path, data.parent_path);
            this.renderStats(data.file_count, data.directory_count, data.total_size);
            this.renderFileList(data.items);
            this.updateURL(this.currentPath);

        } catch (error) {
            this.showError(`Failed to load directory: ${error.message}`);
        } finally {
            this.hideLoading();
        }
    }

    async performSearch() {
        const query = this.elements.searchInput.value.trim();
        if (!query) {
            return;
        }

        this.showLoading();
        this.hideError();
        this.isSearchMode = true;

        try {
            const searchRequest = {
                query: query,
                path: this.currentPath,
                include_subdirectories: true
            };

            const response = await fetch('/api/filesystem/search', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(searchRequest)
            });

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }

            const results = await response.json();

            this.renderBreadcrumb(this.currentPath, null, `Search results for "${query}"`);
            this.renderStats(results.filter(r => !r.is_directory).length, results.filter(r => r.is_directory).length, results.reduce((sum, r) => sum + r.size, 0));
            this.renderFileList(results);
            this.updateURL(this.currentPath, query);

        } catch (error) {
            this.showError(`Search failed: ${error.message}`);
        } finally {
            this.hideLoading();
        }
    }

    clearSearch() {
        this.elements.searchInput.value = '';
        this.loadDirectory();
    }

    refreshCurrentView() {
        if (this.isSearchMode) {
            this.performSearch();
        } else {
            this.loadDirectory();
        }
    }

    renderBreadcrumb(currentPath, parentPath, title = null) {
        const breadcrumb = this.elements.breadcrumb;
        breadcrumb.innerHTML = '';

        if (title) {
            breadcrumb.innerHTML = `<span>${title}</span>`;
            return;
        }

        const pathParts = currentPath === '/' ? [''] : currentPath.split('/').filter(Boolean);

        // Home link
        const homeLink = document.createElement('a');
        homeLink.className = 'breadcrumb-item';
        homeLink.textContent = 'Home';
        homeLink.onclick = () => this.loadDirectory('/');
        breadcrumb.appendChild(homeLink);

        // Path parts
        let buildPath = '';
        pathParts.forEach((part, index) => {
            breadcrumb.appendChild(document.createTextNode(' / '));

            buildPath += '/' + part;
            const link = document.createElement('a');
            link.className = 'breadcrumb-item';
            link.textContent = part;
            link.onclick = () => this.loadDirectory(buildPath);
            breadcrumb.appendChild(link);
        });
    }

    renderStats(fileCount, directoryCount, totalSize) {
        this.elements.stats.textContent = 
            `${directoryCount} directories, ${fileCount} files, ${this.formatFileSize(totalSize)} total`;
    }

    renderFileList(items) {
        const fileList = this.elements.fileList;
        fileList.innerHTML = '';

        if (items.length === 0) {
            const emptyItem = document.createElement('li');
            emptyItem.className = 'file-item';
            emptyItem.innerHTML = '<div class="file-info"><span class="file-name">No items found</span></div>';
            fileList.appendChild(emptyItem);
            return;
        }

        items.forEach(item => {
            const listItem = this.createFileListItem(item);
            fileList.appendChild(listItem);
        });
    }

    createFileListItem(item) {
        const listItem = document.createElement('li');
        listItem.className = 'file-item';
        listItem.dataset.path = item.path;

        const typeIndicator = item.is_directory ? '[DIR]' : '[FILE]';

        listItem.innerHTML = `
            <div class="file-type">${typeIndicator}</div>
            <div class="file-info">
                <span class="file-name" title="${item.name}">${item.name}</span>
                <span class="file-size">${item.is_directory ? '' : this.formatFileSize(item.size)}</span>
                <span class="file-date">${this.formatDate(item.last_modified)}</span>
            </div>
            <div class="file-actions">
                ${item.is_directory ? 
                    '<span style="width: 65px; display: inline-block;"></span>' : 
                    `<button class="action-btn btn-primary" onclick="browser.downloadFile('${item.path}')">Download</button>`
                }
                <button class="action-btn btn-danger" onclick="browser.deleteItem('${item.path}')">Delete</button>
            </div>
        `;

        // Click handler for navigation
        listItem.onclick = (e) => {
            if (e.target.closest('.file-actions')) {
                return; // Don't navigate if clicking on action buttons
            }

            if (item.is_directory) {
                this.loadDirectory(item.path);
            } else {
                // For files, select them
                this.toggleSelection(listItem);
            }
        };

        return listItem;
    }

    toggleSelection(listItem) {
        listItem.classList.toggle('selected');
        const path = listItem.dataset.path;

        if (listItem.classList.contains('selected')) {
            this.selectedItems.add(path);
        } else {
            this.selectedItems.delete(path);
        }
    }

    formatFileSize(bytes) {
        if (bytes === 0) return '0 B';

        const units = ['B', 'KB', 'MB', 'GB', 'TB'];
        const k = 1024;
        const i = Math.floor(Math.log(bytes) / Math.log(k));

        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + units[i];
    }

    formatDate(dateString) {
        const date = new Date(dateString);
        return date.toLocaleDateString() + ' ' + date.toLocaleTimeString([], {hour: '2-digit', minute:'2-digit'});
    }

    // File upload handlers
    handleDragOver(e) {
        e.preventDefault();
        this.elements.uploadArea.classList.add('dragover');
    }

    handleDragLeave(e) {
        e.preventDefault();
        this.elements.uploadArea.classList.remove('dragover');
    }

    handleDrop(e) {
        e.preventDefault();
        this.elements.uploadArea.classList.remove('dragover');

        const files = Array.from(e.dataTransfer.files);
        this.handleFileUpload(files);
    }

    async handleFileUpload(files) {
        if (!files || files.length === 0) return;

        for (let file of files) {
            await this.uploadFile(file);
        }

        // Refresh the current view to show uploaded files
        this.refreshCurrentView();

        // Clear file input
        this.elements.fileInput.value = '';
    }

    async uploadFile(file) {
        try {
            const formData = new FormData();
            formData.append('file', file);
            formData.append('targetDirectory', this.currentPath);

            const response = await fetch('/api/filesystem/upload', {
                method: 'POST',
                body: formData
            });

            if (!response.ok) {
                throw new Error(`Upload failed: ${response.statusText}`);
            }

            const result = await response.json();
            console.log(`Uploaded: ${result.path}`);

        } catch (error) {
            this.showError(`Failed to upload ${file.name}: ${error.message}`);
        }
    }

    async downloadFile(filePath) {
        try {
            const response = await fetch(`/api/filesystem/download?path=${encodeURIComponent(filePath)}`);

            if (!response.ok) {
                throw new Error(`Download failed: ${response.statusText}`);
            }

            const blob = await response.blob();
            const url = window.URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = filePath.split('/').pop();
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            window.URL.revokeObjectURL(url);

        } catch (error) {
            this.showError(`Failed to download file: ${error.message}`);
        }
    }

    async deleteItem(itemPath) {
        if (!confirm(`Are you sure you want to delete "${itemPath}"?`)) {
            return;
        }

        try {
            const response = await fetch(`/api/filesystem/delete?path=${encodeURIComponent(itemPath)}`, {
                method: 'DELETE'
            });

            if (!response.ok) {
                throw new Error(`Delete failed: ${response.statusText}`);
            }

            // Refresh the current view
            this.refreshCurrentView();

        } catch (error) {
            this.showError(`Failed to delete item: ${error.message}`);
        }
    }
}

// Initialize the application
const browser = new FileSystemBrowser();