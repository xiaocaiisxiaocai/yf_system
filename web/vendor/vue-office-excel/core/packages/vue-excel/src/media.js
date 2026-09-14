let imageCache = new WeakMap();
let cacheGeneration = 0;
const renderVersions = new WeakMap();

const clipWidth = 60; // 左侧序号列宽
const clipHeight = 25; // 顶部序号行高
const defaultColWidth = 80;
const defaultRowHeight = 24;
const emuPerCssPixel = 9525;

function getPixelRatio(options) {
    if (Number.isFinite(options?.pixelRatio) && options.pixelRatio > 0) {
        return options.pixelRatio;
    }
    return window.devicePixelRatio || 1;
}

function getColumnWidth(sheet, index, widthOffset) {
    const column = sheet?._columns?.[index];
    if (column?._hidden) {
        return 0.1;
    }
    const width = Number.isFinite(column?.width) && column.width > 0
        ? column.width * 6
        : defaultColWidth;
    return width + (widthOffset || 0);
}

function getRowHeight(sheet, index, heightOffset) {
    const row = sheet?._rows?.[index];
    if (row?._hidden) {
        return 0.1;
    }
    const height = Number.isFinite(row?.height) && row.height > 0
        ? row.height * 4 / 3
        : defaultRowHeight;
    return height + (heightOffset || 0);
}

function calcAnchorPosition(sheet, anchor, options) {
    const {
        nativeCol = 0,
        nativeColOff = 0,
        nativeRow = 0,
        nativeRowOff = 0,
    } = anchor || {};
    let x = clipWidth;
    let y = clipHeight;
    for (let i = 0; i < nativeCol; i++) {
        x += getColumnWidth(sheet, i, options.widthOffset);
    }
    for (let i = 0; i < nativeRow; i++) {
        y += getRowHeight(sheet, i, options.heightOffset);
    }
    return {
        x: x + nativeColOff / emuPerCssPixel,
        y: y + nativeRowOff / emuPerCssPixel,
    };
}

export function calcPosition(sheet, range = {}, offset, options = {}) {
    const pixelRatio = getPixelRatio(options);
    const start = calcAnchorPosition(sheet, range.tl, options);
    let width = 0;
    let height = 0;

    if (range.br) {
        const end = calcAnchorPosition(sheet, range.br, options);
        width = Math.max(0, end.x - start.x);
        height = Math.max(0, end.y - start.y);
    } else if (range.ext) {
        // ExcelJS exposes one-cell anchor extents in CSS pixels (96 DPI).
        width = Math.max(0, Number(range.ext.width) || 0);
        height = Math.max(0, Number(range.ext.height) || 0);
    }

    return {
        x: (start.x - (offset?.scroll?.x || 0)) * pixelRatio,
        y: (start.y - (offset?.scroll?.y || 0)) * pixelRatio,
        width: width * pixelRatio,
        height: height * pixelRatio,
        pixelRatio,
    };
}

export function toImageBytes(data) {
    const source = data?.buffer;
    if (source instanceof ArrayBuffer) {
        return source;
    }
    if (ArrayBuffer.isView(source)) {
        return new Uint8Array(source.buffer, source.byteOffset, source.byteLength);
    }
    throw new TypeError('Invalid image buffer');
}

function isCurrentRender(ctx, token) {
    return token.cacheGeneration === cacheGeneration
        && renderVersions.get(ctx) === token.renderVersion;
}

export function renderImage(ctx, medias, sheet, offset, options = {}) {
    if (!ctx) {
        return;
    }
    const renderVersion = (renderVersions.get(ctx) || 0) + 1;
    renderVersions.set(ctx, renderVersion);
    const token = {cacheGeneration, renderVersion};

    (sheet?._media || []).forEach(media => {
        const {imageId, range, type} = media;
        if (type === 'image') {
            const position = calcPosition(sheet, range, offset, options);
            drawImage(ctx, medias?.[imageId], position, token);
        }
    });
}

export function clearCache() {
    cacheGeneration += 1;
    imageCache = new WeakMap();
}

function drawImage(ctx, data, position, token) {
    getImage(data).then(image => {
        if (!isCurrentRender(ctx, token) || (ctx.canvas && !ctx.canvas.isConnected)) {
            return;
        }
        let sx = 0;
        let sy = 0;
        let sWidth = image.width;
        let sHeight = image.height;
        let dx = position.x;
        let dy = position.y;
        let dWidth = position.width;
        let dHeight = position.height;
        const scaleX = dWidth / sWidth;
        const scaleY = dHeight / sHeight;
        const clipX = clipWidth * position.pixelRatio;
        const clipY = clipHeight * position.pixelRatio;

        if (!Number.isFinite(scaleX) || !Number.isFinite(scaleY) || scaleX <= 0 || scaleY <= 0) {
            return;
        }
        if (dx < clipX) {
            const diff = clipX - dx;
            dx = clipX;
            dWidth -= diff;
            sWidth -= diff / scaleX;
            sx += diff / scaleX;
        }
        if (dy < clipY) {
            const diff = clipY - dy;
            dy = clipY;
            dHeight -= diff;
            sHeight -= diff / scaleY;
            sy += diff / scaleY;
        }
        if (dWidth <= 0 || dHeight <= 0 || sWidth <= 0 || sHeight <= 0) {
            return;
        }
        // Coordinates already use devicePixelRatio, matching x-spreadsheet's canvas.
        ctx.drawImage(image, sx, sy, sWidth, sHeight, dx, dy, dWidth, dHeight);
    }).catch(e => {
        console.error(e);
    });
}

function getImage(data) {
    if (!data || typeof data !== 'object') {
        return Promise.reject(new TypeError('Missing image data'));
    }
    const cached = imageCache.get(data);
    if (cached) {
        return cached;
    }

    const ownerCache = imageCache;
    const promise = new Promise((resolve, reject) => {
        let url;
        try {
            const blob = new Blob([toImageBytes(data)], {type: `image/${data.extension || 'png'}`});
            url = URL.createObjectURL(blob);
            const image = new Image();
            image.onload = () => {
                URL.revokeObjectURL(url);
                resolve(image);
            };
            image.onerror = e => {
                URL.revokeObjectURL(url);
                ownerCache.delete(data);
                reject(e);
            };
            image.src = url;
        } catch (e) {
            if (url) {
                URL.revokeObjectURL(url);
            }
            ownerCache.delete(data);
            reject(e);
        }
    });
    ownerCache.set(data, promise);
    return promise;
}
