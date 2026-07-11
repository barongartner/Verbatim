'use strict';

const { contextBridge, ipcRenderer, webUtils } = require('electron');

contextBridge.exposeInMainWorld('verbatim', {
  getState: () => ipcRenderer.invoke('state:get'),
  setSettings: (partial) => ipcRenderer.invoke('settings:set', partial),

  transcribe: (pcmBuffer, options) =>
    ipcRenderer.invoke('transcribe:run', { pcm: pcmBuffer, options }),
  cancelTranscribe: () => ipcRenderer.invoke('transcribe:cancel'),
  onTranscribeProgress: (cb) => {
    ipcRenderer.on('transcribe:progress', (_e, p) => cb(p));
  },
  onModelProgress: (cb) => {
    ipcRenderer.on('models:progress', (_e, p) => cb(p));
  },

  readFile: (path) => ipcRenderer.invoke('file:read', path),
  saveTextFile: (opts) => ipcRenderer.invoke('file:saveText', opts),

  saveProject: (project) => ipcRenderer.invoke('project:save', project),
  loadProject: (id) => ipcRenderer.invoke('project:load', id),
  deleteProject: (id) => ipcRenderer.invoke('project:delete', id),
  exportProjectFile: (project) => ipcRenderer.invoke('project:exportFile', project),
  openProjectDialog: () => ipcRenderer.invoke('project:openDialog'),

  showInFolder: (p) => ipcRenderer.invoke('shell:showFolder', p),
  openModelsFolder: () => ipcRenderer.invoke('shell:openModelsFolder'),

  pathForFile: (file) => {
    try { return webUtils.getPathForFile(file); } catch { return ''; }
  }
});
