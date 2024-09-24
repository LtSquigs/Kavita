import { MangaFormat } from './manga-format';

export interface MangaFile {
  id: number;
  filePath: {
    path: string;
    pageRange: string;
    fileSize: number;
    cover: string;
  };
  pages: number;
  format: MangaFormat;
  created: string;
  bytes: number;
}
