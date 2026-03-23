// Resize Observer for responsive chart rendering

let resizeObserver = null;
let observedElements = new Map();

export function observeResize(element, dotNetRef) {
    if (!element) {
        console.error('Resize observer: element is null');
        return;
    }

    // Create ResizeObserver if it doesn't exist
    if (!resizeObserver) {
        resizeObserver = new ResizeObserver((entries) => {
            for (const entry of entries) {
                const dotNetObj = observedElements.get(entry.target);
                if (dotNetObj) {
                    const { width, height } = entry.contentRect;
                    try {
                        dotNetObj.invokeMethodAsync('OnContainerResized', Math.floor(width), Math.floor(height));
                    } catch (error) {
                        console.error('Error invoking OnContainerResized:', error);
                    }
                }
            }
        });
    }

    // Store the dotNetRef and observe the element
    observedElements.set(element, dotNetRef);
    resizeObserver.observe(element);
}

export function unobserveResize(element) {
    if (resizeObserver && element) {
        resizeObserver.unobserve(element);
        observedElements.delete(element);
    }
}

export function dispose() {
    if (resizeObserver) {
        resizeObserver.disconnect();
        resizeObserver = null;
        observedElements.clear();
    }
}
