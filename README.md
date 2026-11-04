# MoniteurJeu

Moniteur PC + mode jeu automatique pour Windows, dans la zone de notification. Très léger (~0,003 % CPU, ~9 Mo RAM au repos).

## Fonctions
- Température / charge CPU, GPU NVIDIA, RAM ; alertes de surchauffe et de bridage
- Mode jeu automatique : priorité haute, mode d'alimentation performance, services et applis en pause, RAM en veille vidée, carte NVIDIA forcée
- Overlay en jeu + compteur FPS sans injection (ETW)
- Résumé de chaque session et page d'historique (24 h / 7 jours)
- Bouton « Vider la RAM », fenêtre de réglages, profil batterie

## Compiler
Lancer `Build.bat` (utilise le compilateur C# fourni avec Windows, rien à installer).

Certaines fonctions (FPS, services, vider la RAM) demandent les droits administrateur.
