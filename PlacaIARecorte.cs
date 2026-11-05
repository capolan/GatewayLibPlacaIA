using OpenCvSharp;

namespace GatewayProxy.PlacaIA
{
    // Recorta e amplia a regiao do retangulo verde (o que o LPR/camera desenha em volta da placa).
    // Em fotos grandes (2560x1920) a placa ocupa ~60 px e a IA erra caracteres; o recorte ampliado
    // devolve resolucao util sem perder o contexto, que segue indo na imagem inteira.
    public static class PlacaIARecorte
    {
        // Verde puro do retangulo: matiz ~60 (escala 0-179 do OpenCV), saturacao e brilho altos.
        // Folhagem e luz do dia ficam fora (matiz mais amarelado e menos saturado).
        private static readonly Scalar VerdeMin = new(50, 180, 150);
        private static readonly Scalar VerdeMax = new(70, 255, 255);

        // Devolve o JPEG do recorte ampliado, ou null se nao achou um retangulo verde plausivel
        // (nesse caso o chamador usa so a imagem inteira).
        public static byte[]? Gerar(byte[] jpeg, out string info, double margem = 0.35, int ladoAlvo = 900, int maxAmpliacao = 6)
        {
            using var mat = Cv2.ImDecode(jpeg, ImreadModes.Color);
            if (mat.Empty())
            {
                info = "imagem invalida";
                return null;
            }
            var box = AcharRetangulo(mat, out info);
            if (box == null) { return null; }

            // Margem em volta, para a placa nao ficar encostada na borda do recorte.
            var r = box.Value;
            int mx = Math.Max(12, (int)(r.Width * margem));
            int my = Math.Max(12, (int)(r.Height * margem));
            int x0 = Math.Max(0, r.X - mx), y0 = Math.Max(0, r.Y - my);
            int x1 = Math.Min(mat.Width, r.Right + mx), y1 = Math.Min(mat.Height, r.Bottom + my);
            var roi = new Rect(x0, y0, x1 - x0, y1 - y0);

            using var recorte = new Mat(mat, roi);
            double fator = Math.Clamp((double)ladoAlvo / Math.Max(roi.Width, roi.Height), 1.0, maxAmpliacao);
            using var ampliado = new Mat();
            Cv2.Resize(recorte, ampliado, new Size((int)(roi.Width * fator), (int)(roi.Height * fator)), 0, 0, InterpolationFlags.Cubic);
            Cv2.ImEncode(".jpg", ampliado, out var saida, new ImageEncodingParam(ImwriteFlags.JpegQuality, 95));
            info = $"retangulo {r.Width}x{r.Height} em ({r.X},{r.Y}); recorte {roi.Width}x{roi.Height} ampliado x{fator:0.0}";
            return saida;
        }

        // Caixa que envolve os pixels verdes do retangulo. O contorno e tracejado: dilata para unir os
        // tracos, agrupa e escolhe o grupo com mais pixels verdes de verdade (descarta ruido).
        public static Rect? AcharRetangulo(Mat bgr, out string info)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            using var mascara = new Mat();
            Cv2.InRange(hsv, VerdeMin, VerdeMax, mascara);

            // Falhas do tracejado: ~1% da largura (min. 15 px, impar) as une sem juntar objetos distantes.
            int k = Math.Max(15, (int)(bgr.Width * 0.01)) | 1;
            using var elemento = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
            using var unida = new Mat();
            Cv2.Dilate(mascara, unida, elemento);
            Cv2.FindContours(unida, out var contornos, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            Rect? melhor = null;
            int melhorPixels = 0;
            foreach (var c in contornos)
            {
                var caixa = Cv2.BoundingRect(c);
                // Placa e pequena: descarta o que e grande demais (ex. faixa verde) ou fino demais.
                double prop = (double)caixa.Width / Math.Max(1, caixa.Height);
                if (caixa.Width < 20 || caixa.Height < 10 || prop < 1.0 || prop > 6.0) { continue; }
                if (caixa.Width > bgr.Width * 0.4 || caixa.Height > bgr.Height * 0.4) { continue; }
                using var parte = new Mat(mascara, caixa);
                int pixels = Cv2.CountNonZero(parte);
                if (pixels > melhorPixels) { melhorPixels = pixels; melhor = caixa; }
            }

            if (melhor == null) { info = "retangulo verde nao encontrado"; return null; }
            // A dilatacao engordou a caixa em ~k/2 por lado: encolhe de volta.
            var b = melhor.Value;
            int d = k / 2;
            var justo = new Rect(b.X + d, b.Y + d, Math.Max(1, b.Width - 2 * d), Math.Max(1, b.Height - 2 * d));
            info = $"{melhorPixels} px verdes";
            return justo;
        }
    }
}
