import React from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './style.css';

const root = document.getElementById('root');
if (!root) throw new Error('Missing Mission Control root element');

createRoot(root).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
