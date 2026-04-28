let editor = null;

// Where to load monaco editor modules from — set by the host page (loader script onload).
// Falls back to jsdelivr CDN if the host page didn't set it.
window.__MONACO_VS ||= 'https://cdn.jsdelivr.net/npm/monaco-editor@0.45.0/min/vs';

function _attachMonaco(elementId, dotnetRef) {
    editor = monaco.editor.create(document.getElementById(elementId), {
        value: '',
        language: 'json',
        theme: 'vs-dark',
        automaticLayout: true,
        fontSize: 14,
        minimap: { enabled: true },
        lineNumbers: 'on',
        tabSize: 2,
        formatOnPaste: true,
        scrollBeyondLastLine: false,
        wordWrap: 'on'
    });

    // Ctrl+Z / Ctrl+Y: 코드 에디터 포커스가 아닐 때만 앱 undo/redo 호출
    document.addEventListener('keydown', function (e) {
        if (editor && editor.hasTextFocus()) return; // Monaco가 자체 undo 처리
        if (e.ctrlKey && !e.shiftKey && e.key === 'z') {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnUndoKeyboard');
        } else if (e.ctrlKey && (e.key === 'y' || (e.shiftKey && e.key === 'Z'))) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnRedoKeyboard');
        }
    });
}

window.MonacoInterop = {
    init: function (elementId, dotnetRef) {
        var start = function () {
            require.config({ paths: { 'vs': window.__MONACO_VS } });
            require(['vs/editor/editor.main'], function () { _attachMonaco(elementId, dotnetRef); });
        };
        if (typeof require === 'function') { return start(); }
        // loader.js not yet ready — poll briefly (e.g. fallback-to-CDN path still loading).
        var t = 0;
        var wait = function () {
            if (typeof require === 'function') return start();
            if ((t += 50) > 10000) return console.error('Monaco loader.js did not initialize within 10s');
            setTimeout(wait, 50);
        };
        wait();
    },

    getValue: function () {
        return editor ? editor.getValue() : '';
    },

    setValue: function (value) {
        if (editor) {
            editor.setValue(value);
        }
    },

    formatDocument: function () {
        if (editor) {
            editor.getAction('editor.action.formatDocument').run();
        }
    },

    dispose: function () {
        if (editor) {
            editor.dispose();
            editor = null;
        }
    },

    revealJsonPath: function (jsonPath) {
        if (!editor) return;
        var text = editor.getValue();
        var segments = [];
        jsonPath.replace(/([^\.\[\]]+)/g, function (m) { segments.push(m); });

        var pos = 0;
        for (var i = 0; i < segments.length; i++) {
            var seg = segments[i];
            if (/^\d+$/.test(seg)) {
                var idx = parseInt(seg);
                var depth = 0, count = -1;
                for (var j = pos; j < text.length; j++) {
                    var ch = text[j];
                    if (ch === '[' || ch === '{') {
                        if (depth === 0) count++;
                        if (count === idx && depth === 0) { pos = j; break; }
                        depth++;
                    } else if (ch === ']' || ch === '}') { depth--; }
                }
            } else {
                var found = text.indexOf('"' + seg + '"', pos);
                if (found >= 0) pos = found;
            }
        }

        var line = 1;
        for (var k = 0; k < pos; k++) { if (text[k] === '\n') line++; }

        editor.revealLineInCenter(line);
        editor.setPosition({ lineNumber: line, column: 1 });
        editor.focus();
    },

    downloadFile: function (fileName, base64) {
        var a = document.createElement('a');
        a.href = 'data:application/octet-stream;base64,' + base64;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }
};

// ===== Inline JSON Editor (Property 값용 보조 Monaco 인스턴스) =====
window.InlineJsonEditor = (function () {
    var inst = null;
    var hostId = null;
    var dotnetRef = null;
    var changeListener = null;

    function _create(elementId, value, ref) {
        var el = document.getElementById(elementId);
        if (!el) return false;
        inst = monaco.editor.create(el, {
            value: value || '',
            language: 'json',
            theme: 'vs-dark',
            automaticLayout: true,
            fontSize: 13,
            minimap: { enabled: false },
            lineNumbers: 'on',
            tabSize: 2,
            scrollBeyondLastLine: false,
            wordWrap: 'on',
            formatOnPaste: true
        });
        hostId = elementId;
        dotnetRef = ref;
        changeListener = inst.onDidChangeModelContent(function () {
            if (!dotnetRef) return;
            var raw = inst.getValue();
            var ok = false;
            try { JSON.parse(raw); ok = true; } catch (e) { ok = false; }
            dotnetRef.invokeMethodAsync('OnInlineJsonChanged', ok);
        });
        return true;
    }

    return {
        attach: function (elementId, value, ref) {
            if (inst) {
                try { inst.dispose(); } catch (e) { }
                inst = null;
            }
            var go = function () { _create(elementId, value, ref); };
            if (typeof monaco !== 'undefined') { go(); return; }
            var t = 0;
            var wait = function () {
                if (typeof require === 'function') {
                    require.config({ paths: { 'vs': window.__MONACO_VS } });
                    require(['vs/editor/editor.main'], go);
                    return;
                }
                if ((t += 50) > 10000) return;
                setTimeout(wait, 50);
            };
            wait();
        },
        getValue: function () { return inst ? inst.getValue() : ''; },
        setValue: function (value) { if (inst) inst.setValue(value || ''); },
        format: function () {
            if (inst) inst.getAction('editor.action.formatDocument').run();
        },
        dispose: function () {
            if (changeListener) { try { changeListener.dispose(); } catch (e) { } changeListener = null; }
            if (inst) { try { inst.dispose(); } catch (e) { } inst = null; }
            hostId = null;
            dotnetRef = null;
        }
    };
})();

// ===== Drag & Drop =====
window.DropZone = {
    _droppedFiles: null,

    init: function (dotnetRef) {
        var self = this;

        document.addEventListener('dragenter', function (e) {
            if (e.dataTransfer && e.dataTransfer.types.indexOf('Files') >= 0) {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OnDragEnterPage');
            }
        });

        document.addEventListener('dragover', function (e) {
            e.preventDefault();
        });

        document.addEventListener('dragleave', function (e) {
            if (e.clientX <= 0 || e.clientY <= 0 ||
                e.clientX >= window.innerWidth || e.clientY >= window.innerHeight) {
                dotnetRef.invokeMethodAsync('OnDragLeavePage');
            }
        });

        document.addEventListener('drop', function (e) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnDragLeavePage');

            var files = e.dataTransfer.files;
            if (!files || files.length === 0) return;

            var aasxFiles = [];
            var fileNames = [];
            for (var i = 0; i < files.length; i++) {
                if (files[i].name.endsWith('.aasx')) {
                    aasxFiles.push(files[i]);
                    fileNames.push(files[i].name);
                }
            }

            if (fileNames.length === 0) {
                dotnetRef.invokeMethodAsync('OnDropNotify', 'AASX 파일만 지원합니다');
                return;
            }

            self._droppedFiles = aasxFiles;
            dotnetRef.invokeMethodAsync('OnDropFiles', fileNames);
        });
    },

    // drop된 File 객체를 InputFile의 input에 주입하여 Blazor 스트리밍 활용
    triggerInputFile: function (labelSelector) {
        if (!this._droppedFiles || this._droppedFiles.length === 0) return;

        var dt = new DataTransfer();
        for (var i = 0; i < this._droppedFiles.length; i++) {
            dt.items.add(this._droppedFiles[i]);
        }

        var input = document.querySelector(labelSelector + ' input[type=file]');
        if (input) {
            input.files = dt.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }

        this._droppedFiles = null;
    }
};

// ===== Resizable Panel =====
window.ResizeHandle = {
    init: function (handleId, panelId, direction) {
        var handle = document.getElementById(handleId);
        var panel = document.getElementById(panelId);
        if (!handle || !panel) return;

        var startX, startW;
        var dir = direction || 'left'; // 'left' = panel is to the left of handle

        handle.addEventListener('mousedown', function (e) {
            startX = e.clientX;
            startW = panel.offsetWidth;
            e.preventDefault();

            function onMove(e) {
                var delta = e.clientX - startX;
                var newW = dir === 'left' ? startW + delta : startW - delta;
                if (newW >= 150 && newW <= 600) {
                    panel.style.width = newW + 'px';
                }
            }
            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
            }

            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
        });
    }
};

// ===== Finder Column View =====
window.FinderColumns = {
    scrollToEnd: function (id) {
        var el = document.getElementById(id);
        if (el) {
            el.scrollTo({ left: el.scrollWidth, behavior: 'smooth' });
        }
    }
};

window.ClientCount = {
    update: function (text) {
        var el = document.getElementById('client-count-indicator');
        if (el) el.textContent = text;
    },
    initUnload: function (dotnetRef) {
        window.__clientCountRef = dotnetRef;
        window.addEventListener('beforeunload', window.ClientCount._onUnload);
    },
    _onUnload: function () {
        if (window.__clientCountRef) {
            window.__clientCountRef.invokeMethodAsync('OnBeforeUnload');
        }
    },
    dispose: function () {
        window.removeEventListener('beforeunload', window.ClientCount._onUnload);
        window.__clientCountRef = null;
    }
};
